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
/// <param name="Aborted">True when the run stopped early because the embedding endpoint failed — the remaining due pages were deliberately not attempted this run.</param>
public sealed record EmbeddingIndexRun(
    int PagesPending, int PagesEmbedded, int PagesFailed, int ChunksEmbedded, bool Aborted);

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
/// <b>Failure isolation:</b> this class never runs inside a user request. An unreachable
/// endpoint marks the page's state row failed (retried after backoff) and aborts the rest
/// of the run — it cannot break a page save, and search meanwhile degrades to
/// keyword-only. System action throughout: no user audit events (§9.2), and its
/// SaveChanges raise no domain events, so the audit/outbox pipeline is untouched.
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

        // A page is due when no state row records its current revision as embedded.
        var duePages = _db.Pages.Where(p =>
            !_db.PageEmbeddingStates.Any(s => s.PageId == p.Id && s.EmbeddedRevisionNumber == p.CurrentRevisionNumber));

        var pagesPending = await duePages.CountAsync(cancellationToken);

        var backoffCutoff = DateTime.UtcNow - _options.FailureBackoffOrDefault;
        var batch = await duePages
            .Where(p => !_db.PageEmbeddingStates.Any(s =>
                s.PageId == p.Id && s.FailedAttempts > 0 && s.LastAttemptAtUtc > backoffCutoff))
            .OrderBy(p => p.UpdatedAtUtc).ThenBy(p => p.Id)
            .Take(_options.BatchSize)
            .Select(p => new { p.Id, p.CurrentRevisionNumber, p.CurrentContent })
            .ToListAsync(cancellationToken);

        var embedded = 0;
        var failed = 0;
        var chunksEmbedded = 0;
        var aborted = false;

        foreach (var page in batch)
        {
            try
            {
                chunksEmbedded += await IndexPageAsync(page.Id, page.CurrentRevisionNumber, page.CurrentContent, cancellationToken);
                embedded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // §15: exception type + page id only — never chunk text, never the
                // endpoint's echo of the request.
                _logger.LogWarning(ex,
                    "Embedding failed for page {PageId}; marked for retry after backoff and aborting this run ({ExceptionType})",
                    page.Id, ex.GetType().Name);

                await RecordFailureAsync(page.Id, cancellationToken);
                failed++;

                // One endpoint serves every page: after a failure the rest of this batch
                // would almost certainly fail too. Stop, let the next poll retry.
                aborted = true;
                break;
            }
        }

        var run = new EmbeddingIndexRun(pagesPending, embedded, failed, chunksEmbedded, aborted);

        // Counters after the per-page commits above — a rolled-back page never counted.
        DataTelemetry.RecordEmbeddingRun(
            chunksEmbedded, embedded, failed, Math.Max(0, pagesPending - embedded),
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
        state.LastAttemptAtUtc = now;
        state.UpdatedAtUtc = now;

        // One commit per page: chunk rows and the state row that declares them current
        // land together. If the page was edited between our read and this save, the
        // recorded revision no longer matches Page.CurrentRevisionNumber and the next
        // scan simply finds it due again.
        await _db.SaveChangesAsync(cancellationToken);
        return toEmbed.Count;
    }

    private async Task RecordFailureAsync(Guid pageId, CancellationToken cancellationToken)
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

        state.FailedAttempts++;
        state.LastAttemptAtUtc = now;
        state.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string JoinHeadingPath(IReadOnlyList<string> headingPath)
    {
        var joined = string.Join(" > ", headingPath);
        return joined.Length <= 1000 ? joined : joined[..1000];
    }
}
