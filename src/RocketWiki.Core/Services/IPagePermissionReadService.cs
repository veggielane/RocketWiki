using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

/// <summary>
/// Viewer-relative permission reads (design.md §6.6/§8): the batch behind
/// <c>Page.canEdit/canComment/canManageAccess</c>, the §6.6 permission inspector,
/// and the manage-gated restriction listing. Read-only companion to
/// <see cref="IPageReadService"/> — everything here routes through
/// <see cref="EffectivePermissionCalculator"/> (the single implementation of the
/// §6.4 rules); nothing re-derives rule logic.
///
/// Like every service here, authorization inputs are the token-built
/// <see cref="Principal"/> (design.md §6.1) plus, where §6.5.2's admin arm applies,
/// a caller-resolved <c>isInstanceAdmin</c> bool — the same pattern as
/// <see cref="IAccessRuleService"/>. The local User mirror is touched only to
/// resolve display names, never for a decision.
/// </summary>
public interface IPagePermissionReadService
{
    /// <summary>
    /// Batched viewer-permission facts for pages the caller has ALREADY resolved
    /// (i.e. that passed canView upstream — this method exists so `children { canEdit }`
    /// over N pages costs a constant number of queries, not N recomputed walks).
    /// It still computes fresh, fail-closed results per page: ids that don't exist
    /// (or are soft-deleted) are simply omitted, and a page whose canView would now
    /// fail reports CanView=false — callers must treat a missing/false entry as
    /// "deny", never default to allow.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PagePermissionFacts>> GetPermissionFactsAsync(
        IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// design.md §6.6's inspector, gated fail-closed on the CALLER: NotFound when no
    /// such live page exists; Denied(callerViewReason) when the caller themselves
    /// cannot view the page — the inspector must never become a read-around (§6.5),
    /// not even for the principal being inspected, and a Denied here is collapsed to
    /// the same null a NotFound gets after the resolver audits it (§6.7/§7). Found
    /// carries the full non-short-circuiting explanation for <paramref name="subject"/>
    /// (who the caller may only choose freely when they hold instance admin — the
    /// API layer enforces that arm, since only it can resolve the role).
    /// Chain page titles are safe to include: caller canView on the page implies
    /// canView on every ancestor (§6.4 accumulation).
    /// </summary>
    Task<ReadResult<PagePermissionExplanation>> ExplainAsync(
        Guid pageId, Principal caller, Principal subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// The management read path for a page's restrictions (own + inherited, flagged
    /// — design.md §8's `restrictions` sketch), gated by §6.5.2's rule-management
    /// gate (<see cref="RuleManagementGate"/>, the exact check IAccessRuleService's
    /// mutations enforce). Non-managers — and nonexistent pages — get an empty list,
    /// byte-identical to a page with no restrictions (§6.7's absent-not-forbidden).
    /// Deliberately a plain list rather than a ReadResult: this is only reachable
    /// through a Page the caller already resolved (canView passed), so there is no
    /// hidden-existence decision left to audit — an empty listing is the same
    /// "shows less" the grants listing and tree pruning already are, not a denial.
    /// </summary>
    Task<IReadOnlyList<PageRestrictionDetail>> GetRestrictionsAsync(
        Guid pageId, Principal caller, bool callerIsInstanceAdmin, CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-page facts for the viewer-permission fields. <see cref="SpaceRole"/> is
/// exposed so the API layer can apply §6.5.2's admin arm for canManageAccess via
/// <see cref="RuleManagementGate"/> — the instance-admin bool itself never enters
/// this layer's computation (design.md §6.5: no admin flag in the rule engine).
/// </summary>
public sealed record PagePermissionFacts(
    EffectivePermission Permission,
    SpaceRole? SpaceRole,
    bool IsReplicaSpace)
{
    /// <summary>design.md §6.4.2: commenting requires canView (it is not editing) —
    /// and, like every mutation, is blocked on a replica space (§12).</summary>
    public bool CanComment => Permission.CanView && !IsReplicaSpace;
}

/// <summary>A <see cref="RestrictionCheckDetail"/> with the chain page's title
/// attached for display (see ExplainAsync's doc for why titles are leak-safe here).</summary>
public sealed record ExplainedRestriction(
    Guid RuleId,
    Guid PageId,
    string PageTitle,
    PageAction Action,
    string ExpressionJson,
    bool Passed);

/// <summary>
/// The inspector's full answer for one (page, subject) pair. SubjectDisplayName is
/// display-only, resolved from the local User mirror when one exists for the
/// subject's user id and falling back to the id itself (a hypothetical/what-if
/// principal an admin is testing has no mirror row).
/// </summary>
public sealed record PagePermissionExplanation(
    string SubjectUserId,
    string SubjectDisplayName,
    SpaceRole? SpaceRole,
    bool IsReplicaSpace,
    EffectivePermission Permission,
    IReadOnlyList<ExplainedRestriction> ViewRestrictions,
    IReadOnlyList<ExplainedRestriction> EditRestrictions);

/// <summary>
/// One restriction rule in the management read path: which chain page it sits on
/// (Inherited = attached to an ancestor rather than the requested page), the rule
/// content, and bookkeeping for the management UI. UpdatedByDisplayName comes from
/// the local User mirror — display only, per design.md §6.1.
/// </summary>
public sealed record PageRestrictionDetail(
    Guid RuleId,
    Guid PageId,
    string PageTitle,
    bool Inherited,
    PageAction Action,
    string ExpressionJson,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? UpdatedByDisplayName);
