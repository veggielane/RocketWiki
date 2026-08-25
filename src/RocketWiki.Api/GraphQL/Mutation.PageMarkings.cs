using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Thin GraphQL layer over <see cref="IPageMarkingService"/> (design.md §21) — see
/// Mutation.cs's class doc for the shared plumbing.
///
/// <para><b>One mutation, two audit actions.</b> The declared <c>[AuditAction]</c> is
/// <c>page.marking.set</c>, which is what the field-level middleware and any denial row
/// carry; the success row's action is decided by the domain-event mapper, which writes
/// <c>page.marking.downgrade</c> when the change widens the audience. That split is
/// deliberate rather than a wart: the declaration guard (AuditCoverageTests) needs one
/// static action per field, and whether a change is a downgrade is only knowable after
/// the before-state has been read — which happens inside the transaction, not at the
/// resolver. A denial has no before/after at all, so <c>set</c> is the honest name for
/// it.</para>
/// </summary>
public partial class Mutation
{
    [AuditAction("page.marking.set")]
    public async Task<SetPageMarkingPayload> SetPageMarking(
        SetPageMarkingRequest input,
        [Service] IPageMarkingService markingService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new SetPageMarkingPayload(null, unauthenticated);
        }

        var result = await markingService.SetAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.marking.set", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new SetPageMarkingPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new SetPageMarkingPayload(result.Value, null);
    }
}

public sealed record SetPageMarkingPayload(PageMarkingView? Marking, PageMutationErrorView? Error);
