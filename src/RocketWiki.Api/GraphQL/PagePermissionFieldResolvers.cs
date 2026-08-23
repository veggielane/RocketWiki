using HotChocolate;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// design.md §6.6's "permissions shape the UI" read path: viewer-relative facts
/// (<c>canEdit</c>/<c>canComment</c>/<c>canManageAccess</c>) and the manage-gated
/// restriction listing, on Page. Kept in its own file (not PageFieldResolvers) so
/// parallel schema work merges cleanly.
///
/// Audit stance, stated once for the three boolean fields (design.md §7): they are
/// viewer-relative facts about a page the caller has ALREADY resolved — resolving
/// the Page at all required canView and produced the page.view row (root field /
/// content resolution, deduplicated per request). No new subject is touched and no
/// further access decision is disclosed beyond what the caller's own request already
/// established, so these fields deliberately emit no additional audit event; §7's
/// unit of audit is the user's action (viewing the page), not each derived fact
/// about it. The restrictions field is likewise covered by the parent page.view;
/// its manage gate returning [] is a pruned listing (the same "shows less" as
/// Space.grants and tree pruning, §6.7), not a deniable access attempt.
/// </summary>
public sealed class PagePermissionFieldResolvers
{
    /// <summary>Full §6.4 computation for the current caller, replica invariant
    /// included (§12): unconditionally false on a replica space, beneath every grant.
    /// A missing loader entry (mid-request race) is deny, never default-allow.</summary>
    public async Task<bool> GetCanEditAsync(
        [Parent] Page page, PagePermissionFactsDataLoader factsLoader, CancellationToken cancellationToken)
    {
        var facts = await factsLoader.LoadAsync(page.Id, cancellationToken);
        return facts?.Permission.CanEdit ?? false;
    }

    /// <summary>design.md §6.4.2: commenting requires canView, not canEdit — but like
    /// every mutation it is blocked on a replica space (§12).</summary>
    public async Task<bool> GetCanCommentAsync(
        [Parent] Page page, PagePermissionFactsDataLoader factsLoader, CancellationToken cancellationToken)
    {
        var facts = await factsLoader.LoadAsync(page.Id, cancellationToken);
        return facts?.CanComment ?? false;
    }

    /// <summary>design.md §6.5.2's rule-management gate — instance admin OR this
    /// space's space-admin — via <see cref="RuleManagementGate"/>, the same single
    /// definition IAccessRuleService's mutations enforce. The admin arm is resolved
    /// here from the token's realm roles (IInstanceRoleAccessor), never inside the
    /// rule engine (§6.5: no admin flag in the calculator).</summary>
    public async Task<bool> GetCanManageAccessAsync(
        [Parent] Page page,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        PagePermissionFactsDataLoader factsLoader,
        CancellationToken cancellationToken)
    {
        var facts = await factsLoader.LoadAsync(page.Id, cancellationToken);
        if (facts is null)
        {
            return false;
        }

        return RuleManagementGate.CanManageRules(facts.SpaceRole, instanceRoleAccessor.IsInstanceAdmin);
    }

    /// <summary>
    /// design.md §8's `restrictions` (own + inherited, flagged), readable only
    /// through §6.5.2's rule-management gate — the service applies it and returns []
    /// for everyone else, byte-identical to a page with no restrictions (§6.7).
    ///
    /// Leak-safety of "absent, not forbidden" here, thought through rather than
    /// assumed: canManageAccess is exposed on the same type, so a non-manager CAN
    /// tell why their list is empty — and restriction *existence* on a viewable page
    /// is not a secret anyway (PageTreeNode.hasRestrictions / §6.6's lock badge show
    /// it to viewers by design). What this gate actually protects is the management
    /// detail: edit-rule expressions the caller never passed, rule timestamps, and
    /// author identities. Returning [] rather than a Forbidden error is therefore
    /// consistency with the schema-wide read convention (like Space.grants), not a
    /// load-bearing secrecy mechanism — stated so nobody later "fixes" it into an
    /// error and nobody mistakes it for hiding existence it doesn't hide.
    ///
    /// Not batched (unlike the boolean fields): this is a management surface fetched
    /// for one page at a time by the permissions UI, not rendered in lists — a
    /// constant few queries per page it appears on, accepted and stated.
    /// </summary>
    public async Task<IReadOnlyList<PageRestrictionDetail>> GetRestrictionsAsync(
        [Parent] Page page,
        [Service] IPagePermissionReadService permissionReadService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        return await permissionReadService.GetRestrictionsAsync(
            page.Id, principal, instanceRoleAccessor.IsInstanceAdmin, cancellationToken);
    }
}
