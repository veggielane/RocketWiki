using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Search;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.Assistant;

/// <summary>Why an ask produced no answer — typed payload facts on the GitLab
/// degradation pattern, never GraphQL errors: a failed ask must answer legibly,
/// not error the request.</summary>
public enum AskWikiUnavailableReason
{
    /// <summary>No chat endpoint/model is configured on this instance (§15
    /// fail-closed: unset = feature absent). Nothing was retrieved and nothing
    /// left the process.</summary>
    NotConfigured = 1,

    /// <summary>The model endpoint failed: network error, timeout, or an error
    /// response. Retrieval already ran — the audit row still records what was
    /// sent.</summary>
    Unreachable = 2,

    /// <summary>Permission-filtered retrieval found nothing to ground an answer
    /// in. The model is deliberately NOT called with an empty context — an
    /// ungrounded answer is worse than an honest "nothing found".</summary>
    NoResults = 3,
}

/// <summary>One source the answer cites: a section of a page the asker passed canView
/// for. Validated server-side against the exact context the model was given — the
/// model can only cite what retrieval handed it, so a citation can never name a page
/// the asker couldn't open.</summary>
public sealed record AskWikiCitation(
    Guid PageId,
    string Title,
    IReadOnlyList<string> HeadingPath,
    string AnchorId);

/// <summary>
/// The service-internal ask result (the GraphQL payload plus the record-keeping the
/// resolver needs for §7): <see cref="RetrievedPageIds"/> is the pages whose content
/// was actually placed in the model context — i.e. exactly what traveled to the
/// configured endpoint — and <see cref="CitedPageIds"/> the validated citation
/// subset. Both belong in the assistant.ask audit row's details and nowhere else.
/// </summary>
public sealed record AskWikiOutcome(
    string? Answer,
    IReadOnlyList<AskWikiCitation> Citations,
    AskWikiUnavailableReason? Unavailable,
    string Disposition,
    IReadOnlyList<Guid> RetrievedPageIds,
    IReadOnlyList<Guid> CitedPageIds);

/// <summary>
/// "Ask the wiki" (design.md §9, resolving §17's assistant bullet). The load-bearing
/// rule — <b>answers only from pages the asker can view</b> — is enforced by
/// construction, not by prompt: retrieval runs UNDER THE CALLER'S PRINCIPAL through
/// the same permission-filtered hybrid search every user-facing search uses
/// (<see cref="ISearchService"/>, §6.7/§9.3), and every candidate's content is loaded
/// through the same canView-gated <see cref="IPageReadService"/> the resolvers use,
/// pattern-matching <see cref="ReadResult{T}"/> exactly as they do. A page the asker
/// cannot view is silently absent from the context — so the model cannot leak what
/// retrieval never saw. The LLM is plumbing; access control is the feature.
///
/// A Denied mid-retrieval (race-only in practice: the hit already passed canView
/// inside SearchAsync, so only a rule change landing between the two calls produces
/// one) is audited through the existing <see cref="ReadDenialAudit"/> path as
/// page.view — the same row the GraphQL resolvers write for the same event — and
/// nothing else; NotFound audits nothing, per ReadDenialAudit's doc. Successful
/// retrieval is NOT audited per page: the assistant.ask row's details carry the
/// retrieved page ids (the §7 search.query precedent — one row per user action,
/// with what-was-touched in Details), so per-page page.view Success rows here would
/// double-record one action, exactly what DbAuditSink's dedup exists to prevent
/// between paths.
///
/// §15 flow-of-content, stated honestly: the question and the retrieved (viewable)
/// page content DO travel to the configured chat endpoint — that is the feature.
/// The boundary controls are that the endpoint is fail-closed config (absent by
/// default) and §9.4-in-network by deployment requirement, exactly like the
/// embedding endpoint. Telemetry sees dispositions, counts and durations only —
/// never the question, the context, or a citation (AssistantTelemetryHygieneTests).
/// </summary>
public sealed class AskWikiService(
    ISearchService searchService,
    IPageReadService pageReadService,
    IAuditSink auditSink,
    AssistantOptions? options = null,
    IChatClient? chatClient = null)
{
    /// <summary>Grounding rules. Markers are positional ([S1]…[Sn] in context order),
    /// so an in-range marker is in the retrieved set by construction and everything
    /// else is fabricated and dropped (ValidateCitations).</summary>
    private const string SystemPrompt =
        """
        You are the wiki's built-in assistant. Answer the user's question using ONLY the
        numbered context sections provided in the user message.
        - If the context sections do not contain the information needed, say that you
          could not find the answer in the wiki. Never answer from outside knowledge.
        - Cite the sections you actually used inline by their markers, e.g. [S1] or
          [S2][S4]. Only use markers that appear in the context; never invent one.
        - Be concise.
        """;

    private static readonly Regex MarkerRegex = new(@"\[S(\d{1,4})\]", RegexOptions.Compiled);

    public async Task<AskWikiOutcome> AskAsync(string question, Principal principal, CancellationToken cancellationToken)
    {
        // Constant span name, disposition tag only (§15): the question and every
        // retrieved title/content stay off telemetry entirely.
        using var activity = ApiTelemetry.ActivitySource.StartActivity(ApiTelemetry.AssistantAskSpan);
        var stopwatch = Stopwatch.StartNew();

        AskWikiOutcome Finish(AskWikiOutcome outcome)
        {
            activity?.SetTag(ApiTelemetry.AssistantDispositionTag, outcome.Disposition);
            ApiTelemetry.RecordAssistantAsk(outcome.Disposition, outcome.RetrievedPageIds.Count, stopwatch.Elapsed);
            return outcome;
        }

        if (options is null || chatClient is null)
        {
            return Finish(Unavailable(AskWikiUnavailableReason.NotConfigured,
                ApiTelemetry.AssistantDispositionNotConfigured, []));
        }

        // Retrieval under the caller's own principal: hybrid (keyword + vector when
        // embeddings are configured), over-fetched then canView-filtered inside the
        // service (§9.3). A restricted page is absent from these hits, not filtered here.
        var hits = await searchService.SearchAsync(
            new SearchRequest(question, SpaceKey: null, Labels: null), principal, options.MaxRetrievedPages, cancellationToken);

        // Full content per hit through the same canView-gated read path the resolvers
        // use, pattern-matched per §6.7 — never a raw EF query around the service.
        var pages = new List<(SearchHit Hit, Page Page)>();
        foreach (var hit in hits.DistinctBy(h => h.PageId))
        {
            var result = await pageReadService.GetPageAsync(hit.PageId, principal, cancellationToken);
            switch (result)
            {
                case ReadResult<Page>.Found found:
                    pages.Add((hit, found.Value));
                    break;
                case ReadResult<Page>.Denied denied:
                    // Race-only (see class doc), but a real denial of a real content
                    // read: audited exactly where and how a resolver would audit it,
                    // then silently absent from the context.
                    await ReadDenialAudit.RecordAsync(
                        auditSink, "page.view", AuditSubjectType.Page, hit.PageId, denied.Reason, cancellationToken);
                    break;
                    // NotFound: vanished between search and load — no access decision
                    // was made, nothing to record (ReadDenialAudit's doc).
            }
        }

        var entries = BuildContext(pages, options.MaxContextChars);
        if (entries.Count == 0)
        {
            return Finish(Unavailable(AskWikiUnavailableReason.NoResults,
                ApiTelemetry.AssistantDispositionNoResults, []));
        }

        var retrievedPageIds = entries.Select(e => e.PageId).Distinct().ToArray();

        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, BuildUserMessage(entries, question)),
        ];

        string text;
        try
        {
            var response = await chatClient.GetResponseAsync(messages, options: null, cancellationToken);
            text = response.Text;
        }
        catch (Exception ex) when (IsModelFailure(ex, cancellationToken))
        {
            // Any model-call failure is the same bounded degradation (the GitLab
            // UNREACHABLE reasoning); retrievedPageIds stays in the outcome because
            // the request body — question + context — already traveled.
            return Finish(Unavailable(AskWikiUnavailableReason.Unreachable,
                ApiTelemetry.AssistantDispositionUnreachable, retrievedPageIds));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // A degenerate empty completion is the endpoint misbehaving, not an answer.
            return Finish(Unavailable(AskWikiUnavailableReason.Unreachable,
                ApiTelemetry.AssistantDispositionUnreachable, retrievedPageIds));
        }

        var (answer, citations) = ValidateCitations(text.Trim(), entries);

        return Finish(new AskWikiOutcome(
            answer,
            citations,
            Unavailable: null,
            ApiTelemetry.AssistantDispositionAnswered,
            retrievedPageIds,
            citations.Select(c => c.PageId).Distinct().ToArray()));
    }

    private static AskWikiOutcome Unavailable(
        AskWikiUnavailableReason reason, string disposition, IReadOnlyList<Guid> retrievedPageIds) =>
        new(Answer: null, Citations: [], reason, disposition, retrievedPageIds, CitedPageIds: []);

    /// <summary>One context section as the model sees it, keyed by its 1-based marker position.</summary>
    private sealed record ContextEntry(
        Guid PageId, string Title, IReadOnlyList<string> HeadingPath, string AnchorId, string Text);

    /// <summary>
    /// Builds the context under the char budget, reusing the §9.2 chunker verbatim
    /// (<see cref="MarkdownChunker"/> over <see cref="HeadingAnchors"/> — a second
    /// chunker here would drift from the one the embedding index uses). Selection is
    /// rank-ordered: pass 1 takes each page's hit-attributed chunk (the section
    /// retrieval matched, falling back to the first chunk) in retrieval-rank order;
    /// pass 2 fills remaining budget with the other chunks, same page order, document
    /// order within a page. Chunks that don't fit are skipped, not truncated — a
    /// truncated section invites the model to complete it from outside knowledge.
    /// </summary>
    private static List<ContextEntry> BuildContext(List<(SearchHit Hit, Page Page)> pages, int maxContextChars)
    {
        var entries = new List<ContextEntry>();
        var remaining = maxContextChars;

        var chunked = pages
            .Select(p => (p.Hit, p.Page, Chunks: MarkdownChunker.Chunk(p.Page.CurrentContent)))
            .Where(p => p.Chunks.Count > 0)
            .ToList();

        void TryAdd(Page page, PageChunk chunk)
        {
            if (chunk.Text.Length > remaining)
            {
                return;
            }

            remaining -= chunk.Text.Length;
            entries.Add(new ContextEntry(page.Id, page.Title, chunk.HeadingPath, chunk.AnchorId, chunk.Text));
        }

        var preferred = new List<(Page Page, PageChunk Chunk)>();
        foreach (var (hit, page, chunks) in chunked)
        {
            var attributed = hit.AnchorId.Length > 0
                ? chunks.FirstOrDefault(c => c.AnchorId == hit.AnchorId) ?? chunks[0]
                : chunks[0];
            preferred.Add((page, attributed));
            TryAdd(page, attributed);
        }

        foreach (var (_, page, chunks) in chunked)
        {
            var taken = preferred.First(p => p.Page.Id == page.Id).Chunk;
            foreach (var chunk in chunks)
            {
                if (chunk.Index != taken.Index)
                {
                    TryAdd(page, chunk);
                }
            }
        }

        return entries;
    }

    private static string BuildUserMessage(List<ContextEntry> entries, string question)
    {
        var builder = new StringBuilder("Context sections:\n\n");

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            builder.Append("[S").Append(i + 1).Append("] ").Append(entry.Title);
            if (entry.HeadingPath.Count > 0)
            {
                builder.Append(" — ").Append(string.Join(" > ", entry.HeadingPath));
            }

            builder.Append('\n').Append(entry.Text.Trim()).Append("\n\n");
        }

        builder.Append("Question: ").Append(question);
        return builder.ToString();
    }

    /// <summary>
    /// Server-side citation validation: markers are positions into the context the
    /// model was given, so in-range markers map to retrieved-and-viewable sections by
    /// construction, and out-of-range markers are fabrications — stripped from the
    /// answer text and never returned as citations. Citations come back in order of
    /// first appearance, deduplicated per marker.
    /// </summary>
    private static (string Answer, IReadOnlyList<AskWikiCitation> Citations) ValidateCitations(
        string text, List<ContextEntry> entries)
    {
        var citations = new List<AskWikiCitation>();
        var seen = new HashSet<int>();

        var answer = MarkerRegex.Replace(text, match =>
        {
            var position = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (position < 1 || position > entries.Count)
            {
                return string.Empty; // fabricated marker: dropped, not cited
            }

            if (seen.Add(position))
            {
                var entry = entries[position - 1];
                citations.Add(new AskWikiCitation(entry.PageId, entry.Title, entry.HeadingPath, entry.AnchorId));
            }

            return match.Value;
        });

        return (answer, citations);
    }

    /// <summary>Caller cancellation propagates; everything else — transport failure,
    /// timeout, an error status from the endpoint — is the one bounded UNREACHABLE
    /// (GitLabHttpClient.IsTransportFailure's shape, widened because System.ClientModel
    /// surfaces failures as its own exception types).</summary>
    private static bool IsModelFailure(Exception ex, CancellationToken callerToken) =>
        ex is not OperationCanceledException || !callerToken.IsCancellationRequested;
}
