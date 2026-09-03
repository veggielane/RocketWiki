using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
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
        SetPageMarkingRequestInput input,
        [Service] IPageMarkingService markingService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] IPresenceRuleChangeNotifier ruleChangeNotifier,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new SetPageMarkingPayload(null, unauthenticated);
        }

        var result = await markingService.SetAsync(
            input.ToRequest(), principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.marking.set", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new SetPageMarkingPayload(null, PageMutationErrorView.From(result.Error));
        }

        // design.md §8: an access change has to reach live sessions, not just the
        // database. A marking is the third input to canView (§21), so a page
        // re-marked above a joined co-editor's clearance must evict them - otherwise
        // they keep receiving UpdateReceived, which is page content in CRDT form.
        await ruleChangeNotifier.NotifyRulesChangedAsync(cancellationToken);

        return new SetPageMarkingPayload(result.Value, null);
    }
}

/// <summary>
/// The whole marking, stated in full (design.md §21.6/§21.15): a marking is one value,
/// so every part is a replacement, never a patch. The wire twin of Core's
/// <see cref="SetPageMarkingRequest"/>, differing in one place: <see cref="EyesOnly"/>
/// is the fixed <see cref="NationalCaveatCountry"/> enum, so a caveat outside the five
/// (§21.4) fails schema validation rather than reaching the service. Everything else the
/// service still validates itself — level defined, selectors known and one per category,
/// and the self-lockout rule (you may not set a marking you could not then read).
/// </summary>
/// <param name="EyesOnly">The full replacement caveat set; empty clears it.</param>
/// <param name="Selectors">The full replacement selector set; empty clears it.</param>
/// <param name="UkPrefix">The presentational UK prefix toggle (§21.12); gates nothing.</param>
public sealed record SetPageMarkingRequestInput(
    Guid PageId,
    ClassificationLevel Level,
    IReadOnlyList<NationalCaveatCountry> EyesOnly,
    IReadOnlyList<SelectorValue> Selectors,
    bool UkPrefix)
{
    internal SetPageMarkingRequest ToRequest() => new(
        PageId,
        Level,
        EyesOnly.Select(NationalCaveatCountries.Token).ToList(),
        Selectors,
        UkPrefix);
}

public sealed record SetPageMarkingPayload(PageMarkingView? Marking, PageMutationErrorView? Error);
