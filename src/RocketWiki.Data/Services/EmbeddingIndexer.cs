using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Search;
using RocketWiki.Data.Telemetry;

namespace RocketWiki.Data.Services;

/// <summary>Result of one index run — what the background job records and tests assert on.</summary>
/// <param name="PagesPending">Pages whose current revision awaited (re-)embedding when the scan ran (before this run's work).</param>
/// <param name="Aborted">True when the run stopped early because the embedding endpoint looked down — enough failures in a row that the remaining due pages were deliberately not attempted this run.</param>
/// <param name="PagesQuarantined">Pages whose current revision has exhausted its attempts and is now skipped by the scan, as of the end of this run. A non-zero value is the operator's signal that some page is permanently absent from semantic search until it is edited.</param>
public sealed record EmbeddingIndexRun(
    int PagesPending, int PagesEmbedded, int PagesFailed, int ChunksEmbedded, bool Aborted, int PagesQuarantined = 0);

/// <summary>
/// design.md §9.2 (milestone 7): (re-)embeds pages whose content changed. Driven by the
/// background job in RocketWiki.Api, or called directly by tests with a fake generator.
///
/// <b>Trigger is a state scan, not an event subscription</b> — see
/// <see cref="PageEmbeddingState"/> for why (covers the sync CLI's out-of-process writes,
/// restart-safe, no invasive event-bus plumbing). Chunks are content-hashed so an edit
/// re-embeds only the chunks it changed (§9.2), and chunking runs through
/// <see cref="MarkdownChunker"/>, i.e. the same heading/anchor primitives keyword search
/// hits use.
///
/// <b>Failure isolation:</b> this class never runs inside a user request. A failed page
/// marks its state row (retried after backoff) and the run moves on to the next page —
/// it cannot break a page save, and search meanwhile degrades to keyword-only. Only a
/// run of consecutive failures, which means the endpoint itself is down, abandons the
/// rest of the batch. A page that fails <c>MaxAttempts</c> times against the same
/// revision is quarantined and skipped by the scan until it is edited, because the scan
/// is oldest-first and one permanently-failing page would otherwise win every batch and
/// starve every page behind it; <c>rocketwiki.embeddings.pages_quarantined</c> and a
/// per-page warning are how an operator sees that. System action throughout: no user
/// audit events (§9.2), and its SaveChanges raise no domain events, so the audit/outbox
/// pipeline is untouched.
/// </summary>
public class EmbeddingIndexer
{
    private readonly RocketWikiDbContext _db;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<EmbeddingIndexer> _logger;

    public EmbeddingIndexer(
        RocketWikiDbContext db,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingOptions options,
        ILogger<EmbeddingIndexer> logger)
    {
        _db = db;
        _generator = generator;
        _options = options;
        _logger = logger;
    }

    public async Task<EmbeddingIndexRun> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        using var span = DataTelemetry.StartSpan(DataTelemetry.EmbeddingIndexRunSpan);

        await PurgeDeletedPagesAsync(cancellationToken);

        // A page is due when no state row records its current revision as embedded —
        // and its space is not archived. Archiving sets Space.IsDeleted, and both
        // search legs already exclude those spaces, so embedding their pages produced
        // vectors nothing could ever return. It is not only wasted work: it kept
        // sending the content of archived spaces to the embedding endpoint, which is a
        // poor answer to "we archived that space".
        var duePages = _db.Pages.Where(p =>
            // Existence in _db.Spaces, not "not IsDeleted": the DbSet already carries
            // the soft-delete query filter, so an archived space is simply absent from
            // it and a predicate testing space.IsDeleted here could never match. Asking
            // for existence instead also fails closed on a page whose space row is
            // missing entirely.
            _db.Spaces.Any(space => space.Id == p.SpaceId)
            && !_db.PageEmbeddingStates.Any(s => s.PageId == p.Id && s.EmbeddedRevisionNumber == p.CurrentRevisionNumber));

        var pagesPending = await duePages.CountAsync(cancellationToken);

        var backoffCutoff = DateTime.UtcNow - _options.FailureBackoffOrDefault;
        var maxAttempts = _options.MaxAttemptsOrDefault;

        // Quarantined: this exact revision has already failed MaxAttempts times in a row.
        // The scan is oldest-first, so without this clause a page the endpoint can never
        // embed reappears at the head of every batch forever and no page behind it is
        // ever reached. Keyed on the revision, so an edit that fixes the content lifts
        // the quarantine by itself.
        var quarantined = _db.PageEmbeddingStates.Where(s =>
            s.FailedAttempts >= maxAttempts && s.FailedRevisionNumber == s.Page!.CurrentRevisionNumber);

        var pagesQuarantined = await quarantined.CountAsync(cancellationToken);

        var batch = await duePages
            .Where(p => !_db.PageEmbeddingStates.Any(s =>
                s.PageId == p.Id && s.FailedAttempts > 0 && s.LastAttemptAtUtc > backoffCutoff))
            .Where(p => !quarantined.Any(s => s.PageId == p.Id))
            .OrderBy(p => p.UpdatedAtUtc).ThenBy(p => p.Id)
            .Take(_options.BatchSize)
            .Select(p => new { p.Id, p.CurrentRevisionNumber, p.CurrentContent })
            .ToListAsync(cancellationToken);

        var embedded = 0;
        var failed = 0;
        var chunksEmbedded = 0;
        var aborted = false;
        var consecutiveFailures = 0;
        var abortThreshold = _options.AbortAfterConsecutiveFailuresOrDefault;

        // The pages blamed by the current unbroken run of failures, with the attempt count
        // each had before. If the streak reaches the abort threshold the evidence says the
        // ENDPOINT is down, not that these particular pages are bad — so their attempts are
        // handed back. Without this, an outage walks the batch stamping healthy pages, and
        // a long enough one would push them to MaxAttempts and quarantine content that was
        // never the problem: the attempt ceiling turned from a poison-page guard into an
        // outage amplifier. The fault domain is the endpoint; the counter is per page.
        var streak = new List<(Guid PageId, int PriorAttempts)>();

        foreach (var page in batch)
        {
            try
            {
                chunksEmbedded += await IndexPageAsync(page.Id, page.CurrentRevisionNumber, page.CurrentContent, cancellationToken);
                embedded++;
                // A success proves the endpoint is up, so every failure before it in
                // this run really was that page’s own problem and its count stands.
                consecutiveFailures = 0;
                streak.Clear();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var attempts = await RecordFailureAsync(page.Id, page.CurrentRevisionNumber, cancellationToken);
                failed++;
                consecutiveFailures++;
                streak.Add((page.Id, attempts - 1));

                // §15: exception type + page id + the attempt count only — never chunk
                // text, never the endpoint's echo of the request. The exception OBJECT is
                // deliberately NOT passed to ILogger: that renders it with ToString(), so
                // message and stack enter the record, and the OpenAI client builds its
                // message out of the endpoint's response body — which commonly echoes the
                // rejected input straight back. A log line is the one emission channel
                // §15's hygiene tests cannot intercept, so the type name is all it gets.
                if (attempts >= maxAttempts)
                {
                    // The one line an operator needs to find a poisoned page: everything
                    // else about this state is a count. Warning, not Error: search still
                    // works, this page is simply absent from the semantic half.
                    _logger.LogWarning(
                        "Embedding failed for page {PageId} revision {RevisionNumber} on attempt {Attempts} of {MaxAttempts} ({ExceptionType}); quarantining this revision — it will be retried when the page is next edited",
                        page.Id, page.CurrentRevisionNumber, attempts, maxAttempts, ex.GetType().Name);
                    pagesQuarantined++;
                }
                else
                {
                    _logger.LogWarning(
                        "Embedding failed for page {PageId} revision {RevisionNumber} on attempt {Attempts} of {MaxAttempts} ({ExceptionType}); marked for retry after backoff",
                        page.Id, page.CurrentRevisionNumber, attempts, maxAttempts, ex.GetType().Name);
                }

                // Continue to the next page. One endpoint serves every page, so a run of
                // consecutive failures does mean the endpoint is down and the rest of the
                // batch is pointless — but a single failure between successes means the
                // endpoint is up and this page is the problem, and abandoning the batch
                // there hands one page the power to block every page behind it.
                if (consecutiveFailures >= abortThreshold)
                {
                    // Endpoint-wide fault: give the attempts back before leaving.
                    pagesQuarantined -= await UndoFailureAttemptsAsync(streak, cancellationToken);
                    aborted = true;
                    break;
                }
            }
        }

        var run = new EmbeddingIndexRun(pagesPending, embedded, failed, chunksEmbedded, aborted, pagesQuarantined);

        // Counters after the per-page commits above — a rolled-back page never counted.
        DataTelemetry.RecordEmbeddingRun(
            chunksEmbedded, embedded, failed, Math.Max(0, pagesPending - embedded), pagesQuarantined,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
        span?.SetTag(DataTelemetry.EmbeddingPagesPendingTag, pagesPending);
        span?.SetTag(DataTelemetry.EmbeddingPagesIndexedTag, embedded);
        span?.SetTag(DataTelemetry.EmbeddingChunksEmbeddedTag, chunksEmbedded);
        DataTelemetry.SetOutcome(span, failed > 0 ? "failure" : DataTelemetry.SuccessOutcome);

        return run;
    }

    /// <summary>
    /// Trash leaves no vectors behind: chunk rows and state rows of soft-deleted pages
    /// are removed (the state row's absence is also what re-embeds a page on restore).
    /// IgnoreQueryFilters because the global soft-delete filter on Page would otherwise
    /// hide exactly the rows this is looking for.
    /// </summary>
    private async Task PurgeDeletedPagesAsync(CancellationToken cancellationToken)
    {
        await _db.PageEmbeddings.IgnoreQueryFilters()
            .Where(e => e.Page!.IsDeleted)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PageEmbeddingStates.IgnoreQueryFilters()
            .Where(s => s.Page!.IsDeleted)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <returns>Chunks actually sent to the endpoint (changed or new); unchanged chunks are skipped by hash.</returns>
    private async Task<int> IndexPageAsync(Guid pageId, int revisionNumber, string content, CancellationToken cancellationToken)
    {
        var chunks = MarkdownChunker.Chunk(content);
        var existing = await _db.PageEmbeddings
            .Where(e => e.PageId == pageId)
            .ToListAsync(cancellationToken);
        var existingByIndex = existing.ToDictionary(e => e.ChunkIndex);

        var toEmbed = new List<(PageChunk Chunk, byte[] Hash)>();
        foreach (var chunk in chunks)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chunk.EmbeddingInput));
            var unchanged = existingByIndex.TryGetValue(chunk.Index, out var row)
                && row.Model == _options.Model
                && row.ChunkHash.AsSpan().SequenceEqual(hash);
            if (!unchanged)
            {
                toEmbed.Add((chunk, hash));
            }
        }

        var now = DateTime.UtcNow;

        if (toEmbed.Count > 0)
        {
            var embeddings = await _generator.GenerateAsync(
                toEmbed.Select(t => t.Chunk.EmbeddingInput).ToList(),
                new EmbeddingGenerationOptions { ModelId = _options.Model, Dimensions = _options.Dimensions },
                cancellationToken);

            if (embeddings.Count != toEmbed.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding endpoint returned {embeddings.Count} vectors for {toEmbed.Count} inputs.");
            }

            for (var i = 0; i < toEmbed.Count; i++)
            {
                var vector = embeddings[i].Vector.ToArray();
                if (vector.Length != _options.Dimensions)
                {
                    // Fail the page rather than store a vector that could never be
                    // compared: dimensions are fixed per index (data-model.md).
                    throw new InvalidOperationException(
                        $"Embedding endpoint returned {vector.Length} dimensions, expected {_options.Dimensions}.");
                }

                var (chunk, hash) = toEmbed[i];
                if (existingByIndex.TryGetValue(chunk.Index, out var row))
                {
                    row.HeadingPath = JoinHeadingPath(chunk.HeadingPath);
                    row.ChunkHash = hash;
                    row.Embedding = vector;
                    row.Model = _options.Model;
                    row.UpdatedAtUtc = now;
                }
                else
                {
                    _db.PageEmbeddings.Add(new PageEmbedding
                    {
                        PageId = pageId,
                        ChunkIndex = chunk.Index,
                        HeadingPath = JoinHeadingPath(chunk.HeadingPath),
                        ChunkHash = hash,
                        Embedding = vector,
                        Model = _options.Model,
                        UpdatedAtUtc = now,
                    });
                }
            }
        }

        // The page shrank: drop rows past the new chunk count.
        foreach (var stale in existing.Where(e => e.ChunkIndex >= chunks.Count))
        {
            _db.PageEmbeddings.Remove(stale);
        }

        var state = await _db.PageEmbeddingStates.FindAsync([pageId], cancellationToken)
            ?? _db.PageEmbeddingStates.Add(new PageEmbeddingState { PageId = pageId }).Entity;
        state.EmbeddedRevisionNumber = revisionNumber;
        state.FailedAttempts = 0;
        state.FailedRevisionNumber = 0;
        state.LastAttemptAtUtc = now;
        state.UpdatedAtUtc = now;

        // One commit per page: chunk rows and the state row that declares them current
        // land together. If the page was edited between our read and this save, the
        // recorded revision no longer matches Page.CurrentRevisionNumber and the next
        // scan simply finds it due again.
        await _db.SaveChangesAsync(cancellationToken);
        return toEmbed.Count;
    }

    /// <summary>
    /// Hands back the attempts charged to pages during a failure streak that turned out to
    /// be an endpoint outage. <c>LastAttemptAtUtc</c> is deliberately left alone: the
    /// backoff SHOULD still apply — there is no point hammering a down endpoint — but a
    /// page must not move towards quarantine because the endpoint was unreachable while
    /// its turn came round.
    /// </summary>
    /// <returns>How many of these pages were, wrongly, at or over the quarantine ceiling.</returns>
    private async Task<int> UndoFailureAttemptsAsync(
        IReadOnlyList<(Guid PageId, int PriorAttempts)> streak, CancellationToken cancellationToken)
    {
        if (streak.Count == 0)
        {
            return 0;
        }

        _db.ChangeTracker.Clear();

        var maxAttempts = _options.MaxAttemptsOrDefault;
        var unquarantined = 0;

        foreach (var (pageId, priorAttempts) in streak)
        {
            var state = await _db.PageEmbeddingStates.FindAsync([pageId], cancellationToken);
            if (state is null)
            {
                continue;
            }

            if (state.FailedAttempts >= maxAttempts && priorAttempts < maxAttempts)
            {
                unquarantined++;
            }

            state.FailedAttempts = priorAttempts;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return unquarantined;
    }

    /// <returns>Consecutive failures now recorded against this revision.</returns>
    private async Task<int> RecordFailureAsync(Guid pageId, int revisionNumber, CancellationToken cancellationToken)
    {
        // Discard whatever the failed attempt had staged — only the failure marker
        // may reach the database.
        _db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;
        var state = await _db.PageEmbeddingStates.FindAsync([pageId], cancellationToken);
        if (state is null)
        {
            state = new PageEmbeddingState { PageId = pageId, EmbeddedRevisionNumber = 0 };
            _db.PageEmbeddingStates.Add(state);
        }

        // A failure against a different revision starts the count over: the attempts
        // that came before were counted against content this page no longer has.
        state.FailedAttempts = state.FailedRevisionNumber == revisionNumber ? state.FailedAttempts + 1 : 1;
        state.FailedRevisionNumber = revisionNumber;
        state.LastAttemptAtUtc = now;
        state.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return state.FailedAttempts;
    }

    private static string JoinHeadingPath(IReadOnlyList<string> headingPath)
    {
        var joined = string.Join(" > ", headingPath);
        return joined.Length <= 1000 ? joined : joined[..1000];
    }
}
