using System.Text.Json;
using RocketWiki.Api.Assistant;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Telemetry;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Markings;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The askWiki result: the answer with inline [Sn] markers, the server-validated
/// citations those markers resolve to (every one a page/section the asker passed
/// canView for — see AskWikiService.ValidateCitations), or a typed unavailability.
/// Payload facts, never GraphQL errors — the GitLab degradation pattern: a failed
/// ask must answer legibly, not error the request around it.
///
/// <para><see cref="AggregateMarking"/> is what the answer IS (design.md §21.13): the
/// highest classification among every page that entered the model context, with each
/// distinct eyes-only caveat listed. <b>Anything rendering the answer must render this
/// beside it</b> — an answer synthesized from a SECRET page is SECRET, and the whole
/// reason this field exists is that without it such an answer arrives looking like plain
/// text. Null exactly when <see cref="Answer"/> is null: no text, nothing to mark. Render
/// <c>aggregateMarking.label</c> verbatim; do not compose a string from the parts
/// (§21.1).</para>
///
/// <para>Each citation carries its own source page's marking too, so the bibliography is
/// marked per entry as well as in aggregate.</para>
/// </summary>
public sealed record AskWikiPayload(
    string? Answer,
    IReadOnlyList<AskWikiCitation> Citations,
    AskWikiUnavailableReason? Unavailable,
    AggregateMarkingLabel? AggregateMarking);

/// <summary>
/// What the ask surface needs to know BEFORE anybody asks: whether this instance has an
/// assistant at all, and how long a question it will accept.
///
/// <para><b>Why a status field rather than a field on <see cref="AskWikiPayload"/>.</b>
/// The limit's whole use is a character counter that warns while the question is being
/// typed. A field on the payload only arrives with an answer — i.e. after the ask that
/// the counter existed to prevent — so it would be a number that only ever shows up too
/// late. This is the <c>gitlabStatus</c> shape, for the same reason gitlabStatus has it:
/// instance configuration the client needs in order to render correctly, read once.</para>
///
/// <para><see cref="MaxQuestionChars"/> is <b>null exactly when the assistant is not
/// configured</b>, rather than reporting the code default. There is no limit when there
/// is no feature, and a number quoted for an absent assistant is a number about nothing —
/// the UI's honest state there is the one it already has: no figure at all. (The refusal
/// copy in <c>unavailableCopy.ts</c> deliberately quotes no number today for precisely
/// this reason; with this field it can.)</para>
/// </summary>
public sealed record AssistantStatus(bool Configured, int? MaxQuestionChars);

public partial class Query
{
    /// <summary>
    /// Instance configuration for the ask surface. No content, no wiki state, nobody
    /// else's anything — a bound and a boolean.
    ///
    /// <para><b>Anonymous callers get the absent shape</b> (<c>configured: false</c>, no
    /// limit), which is deliberately stricter than its sibling <c>gitlabStatus</c>. Two
    /// reasons, and the inconsistency is worth stating rather than leaving to be
    /// discovered: <c>askWiki</c> itself refuses anonymous callers outright, so a status
    /// field that answered them would describe a feature they cannot reach; and this
    /// instance has just closed introspection outside Development on the argument that an
    /// unauthenticated, machine-readable inventory of what a classified system runs has no
    /// operational purpose. "This deployment has an AI assistant wired to an endpoint" is
    /// a small entry in that same inventory. It costs the SPA nothing — it reads this
    /// signed in.</para>
    ///
    /// <para><c>gitlabStatus</c>'s looser posture is pre-existing and not changed here;
    /// this field simply does not copy it.</para>
    /// </summary>
    [NoAudit("Reads instance configuration only - whether an assistant endpoint is configured and the question-length bound. No wiki content, no principal attributes, no other user's state (design.md §7). Anonymous callers get the same answer as an unconfigured instance.")]
    public AssistantStatus AssistantStatus(
        [Service] IServiceProvider services,
        [Service] ICurrentPrincipalAccessor principalAccessor)
    {
        // GetService, not GetRequiredService: AssistantConfiguration registers
        // AssistantOptions ONLY when a chat endpoint and model are configured (§15's
        // fail-closed family), so its absence IS "not configured" and must not throw.
        var options = principalAccessor.Current is null ? null : services.GetService<AssistantOptions>();

        return options is null
            ? new AssistantStatus(Configured: false, MaxQuestionChars: null)
            : new AssistantStatus(Configured: true, MaxQuestionChars: options.MaxQuestionChars);
    }

    /// <summary>
    /// "Ask the wiki" (design.md §9, resolving §17's assistant bullet): RAG over the
    /// permission-filtered hybrid index, generation at the configured in-network
    /// endpoint. The whole feature runs under the CALLER's principal — see
    /// AskWikiService's doc for the load-bearing rule and its mechanism.
    ///
    /// Auth is required: unlike the read roots (which answer empty because a silent
    /// shape must not leak), an anonymous ask has nothing to protect by staying
    /// silent and no user to attribute the mandatory §7 row to, so it is refused
    /// loudly with a typed error before any work happens — the same
    /// "Authentication required" refusal MutationAuthHelper gives anonymous
    /// mutations, and deliberately unaudited for the same reason stated there.
    /// </summary>
    [AuditAction("assistant.ask")]
    [UseAuditDispatch]
    public async Task<AskWikiPayload> AskWiki(
        string question,
        [Service] AskWikiService askWikiService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUser,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null || actingUser.ActingUserId is null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("Authentication required.")
                .SetCode("AUTH_NOT_AUTHENTICATED")
                .Build());
        }

        // The audit row is written in a `finally`, not on the success path. AskAsync
        // deliberately rethrows OperationCanceledException when the caller's token trips,
        // and that used to unwind straight past the write below — so a browser navigating
        // away mid-generation meant the question and the retrieved page content had
        // already left the process with ZERO record of who asked what or which pages
        // travelled, on the one path where content crosses the §9.4 boundary. Any other
        // throw from retrieval (the deliberately-propagating vector-dimension mismatch)
        // had the same hole.
        //
        // `attempt` is what makes the row honest rather than merely present: the service
        // fills it in as each fact becomes true, so an abandoned ask still names the pages
        // whose content went to the endpoint.
        var attempt = new AskWikiAttempt();
        AskWikiOutcome? outcome = null;
        try
        {
            outcome = await askWikiService.AskAsync(question, principal, attempt, cancellationToken);
        }
        finally
        {
            await RecordAskAsync(auditSink, question, outcome, attempt);
        }

        return new AskWikiPayload(
            outcome.Answer, outcome.Citations, outcome.Unavailable, outcome.AggregateMarking);
    }

    /// <summary>Bounds one field of one audit row; the refusal in AskWikiService is
    /// the real guard, and this is what keeps the regulated record bounded even when
    /// that guard is bypassed by an exception before it runs.</summary>
    private static string TruncateForAudit(string question)
    {
        const int MaxAuditedQuestionChars = 4000;
        return question.Length <= MaxAuditedQuestionChars
            ? question
            : question[..MaxAuditedQuestionChars] + "… (truncated)";
    }

    private static Task RecordAskAsync(
        IAuditSink auditSink, string question, AskWikiOutcome? outcome, AskWikiAttempt attempt)
    {
        // §7, the search.query precedent verbatim: the question text belongs in the
        // audit row's Details — the grant-protected, append-only record of
        // who-asked-what — and never in telemetry (§15). retrievedPageIds is exactly
        // the set whose content traveled to the model; citedPageIds the validated
        // citations. Outcome is Success for every disposition (the gitlab.fetch
        // reasoning): an unavailable result is still a completed ask, and `denied`
        // stays reserved for ABAC refusals — which retrieval already handled by
        // making restricted pages absent (plus a page.view Denied row on the
        // race-only mid-retrieval path). Explicit rather than left to
        // AuditFieldMiddleware, which cannot know the question belongs in Details;
        // the middleware's own row dedupes against this one inside DbAuditSink.
        // CancellationToken.None, deliberately: the caller's token is very often the
        // reason there is anything unusual to record, so honouring it here would drop
        // exactly the rows that matter most. §7 already requires a failed audit insert to
        // fail the request; it must not be able to skip one instead.
        return auditSink.RecordAsync(
            new AuditRecord(
                "assistant.ask",
                AuditOutcome.Success,
                DetailsJson: JsonSerializer.Serialize(new
                {
                    // Truncated as a backstop. The service refuses an over-long
                    // question before retrieval, so this should never bite — but
                    // AuditEvent.DetailsJson has no length limit of its own, the table
                    // is append-only, and this row is written even when the ask threw
                    // before reaching any of the service’s own guards. A bound that
                    // only exists one layer up is not a bound on what lands here.
                    question = TruncateForAudit(question),
                    // An ask that threw has no disposition of its own — it never reached
                    // the point that records one — so it is named for what it is rather
                    // than borrowed from a completed outcome.
                    disposition = outcome?.Disposition ?? ApiTelemetry.AssistantDispositionAbandoned,
                    retrievedPageIds = outcome?.RetrievedPageIds ?? attempt.RetrievedPageIds,
                    citedPageIds = outcome?.CitedPageIds ?? [],
                    // §21.13/§21.7: what the answer was marked as when it left. Not
                    // recoverable from retrievedPageIds later — markings are a single
                    // mutable row, so re-deriving it after a re-marking would report
                    // today's classification for yesterday's answer. Same reason §21.7
                    // records the before-state of a marking change: for a mutable row,
                    // the audit log is the only history there is. The audit table is also
                    // where a full country set is allowed to appear (§21.7) — and this is
                    // a label built from them.
                    aggregateMarking = outcome?.AggregateMarking?.Label ?? attempt.AggregateMarkingLabel,
                })),
            CancellationToken.None);
    }
}
