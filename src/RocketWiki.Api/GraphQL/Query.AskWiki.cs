using System.Text.Json;
using RocketWiki.Api.Assistant;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The askWiki result: the answer with inline [Sn] markers, the server-validated
/// citations those markers resolve to (every one a page/section the asker passed
/// canView for — see AskWikiService.ValidateCitations), or a typed unavailability.
/// Payload facts, never GraphQL errors — the GitLab degradation pattern: a failed
/// ask must answer legibly, not error the request around it.
/// </summary>
public sealed record AskWikiPayload(
    string? Answer,
    IReadOnlyList<AskWikiCitation> Citations,
    AskWikiUnavailableReason? Unavailable);

public partial class Query
{
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

        var outcome = await askWikiService.AskAsync(question, principal, cancellationToken);

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
        await auditSink.RecordAsync(
            new AuditRecord(
                "assistant.ask",
                AuditOutcome.Success,
                DetailsJson: JsonSerializer.Serialize(new
                {
                    question,
                    disposition = outcome.Disposition,
                    retrievedPageIds = outcome.RetrievedPageIds,
                    citedPageIds = outcome.CitedPageIds,
                })),
            cancellationToken);

        return new AskWikiPayload(outcome.Answer, outcome.Citations, outcome.Unavailable);
    }
}
