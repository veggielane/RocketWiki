using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// design.md §6.4/§8: a Space's own authorization model is simpler than a Page's — no
/// restriction/ancestor-accumulation, just "does this principal hold a SpaceGrant role
/// for this space" — so, unlike Page, calling <see cref="EffectivePermissionCalculator"/>
/// directly from this resolver layer (rather than needing a dedicated space read
/// service) is exactly what the calculator's own doc comment names as an expected
/// direct caller ("GraphQL resolvers... must all route through here").
/// </summary>
public sealed class SpaceFieldResolvers
{
    /// <summary>
    /// The space's designated owner as a display-safe <see cref="UserRef"/>, batched
    /// through <see cref="UserRefByIdDataLoader"/> (design.md §8's DataLoader rule).
    ///
    /// <para><b>Null when the id resolves to nobody</b>, rather than throwing the way the
    /// FK-backed author fields do — see <c>SpaceType</c>'s <c>owner</c> field. The honest
    /// case is a replica materialised by the sync importer, which has no local user to
    /// point at because users do not cross the boundary (§12).</para>
    ///
    /// <para>§6.1 boundary, same as every other UserRef field: this READS the local user
    /// mirror for display and feeds no authorization decision. Ownership grants nothing —
    /// no rule engine path consumes it — so surfacing it alongside the space is
    /// attribution, not a permission signal.</para>
    /// </summary>
    public async Task<UserRef?> GetOwnerAsync(
        [Parent] Space space, UserRefByIdDataLoader userLoader, CancellationToken cancellationToken) =>
        await userLoader.LoadAsync(space.OwnerUserId, cancellationToken);

    /// <summary>
    /// design.md §6.6's "permissions shape the UI": can the caller administer this
    /// space's access — instance admin OR this space's own space-admin — so the client
    /// can render or omit management controls (the owner-reassign control above all)
    /// instead of offering one that fails.
    ///
    /// <para>Routed through <see cref="RuleManagementGate"/>, the single definition
    /// <c>Page.canManageAccess</c> and <c>IAccessRuleService</c>'s mutations already
    /// share, so this read gate cannot drift from the write gate it advertises. The
    /// admin arm comes from the token's realm roles via
    /// <see cref="IInstanceRoleAccessor"/>, never from inside the rule engine (§6.5:
    /// no admin flag in the calculator).</para>
    ///
    /// <para><b>No owner bypass.</b> Being the space's owner grants nothing here, for
    /// the same reason it grants nothing anywhere: ownership is accountability, and
    /// access is the grants. If this field said true for an owner, the UI would show a
    /// reassign control that <c>SetOwnerAsync</c> then refuses — the read gate and the
    /// write gate must give the same answer, which is the whole point of sharing one
    /// definition.</para>
    ///
    /// <para>Batched (<see cref="SpaceRoleBySpaceIdDataLoader"/>) because this is
    /// rendered per row in a space list. No audit: a viewer-relative "what can I do
    /// here" about a space the caller has already resolved discloses no new subject,
    /// matching the stance stated for Page's three boolean fields (§7).</para>
    /// </summary>
    public async Task<bool> GetCanManageAccessAsync(
        [Parent] Space space,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        SpaceRoleBySpaceIdDataLoader roleLoader,
        CancellationToken cancellationToken)
    {
        var role = await roleLoader.LoadAsync(space.Id, cancellationToken);
        return RuleManagementGate.CanManageRules(role, instanceRoleAccessor.IsInstanceAdmin);
    }

    /// <summary>
    /// design.md §12: replica-ness is "origin instance != this instance", computed
    /// against the configured <see cref="InstanceIdentity"/> — the same comparison the
    /// rule engine's read-only invariant uses, now readable by the client so the
    /// "mirrored from LOW — read-only" banner renders proactively instead of only
    /// after a failed write. Visible to every viewer of the space on purpose: §12
    /// specifies that banner for anyone browsing a replica, and the origin id itself
    /// already reaches every such viewer through <c>ReadOnlyReplicaError</c>'s
    /// <c>originInstanceId</c> (§12: "so the client can render 'mirrored from
    /// LOW'") — this reveals nothing a blocked edit didn't already say.
    /// </summary>
    public bool GetIsReplica([Parent] Space space, [Service] InstanceIdentity instanceIdentity) =>
        space.IsReplicaOf(instanceIdentity.LocalInstanceId);

    /// <summary>See <see cref="ViewerWatchesSpaceDataLoader"/> for the viewer-relative
    /// contract and why this emits no audit row of its own.</summary>
    public async Task<bool> GetViewerIsWatchingAsync(
        [Parent] Space space, ViewerWatchesSpaceDataLoader watchLoader, CancellationToken cancellationToken) =>
        await watchLoader.LoadAsync(space.Id, cancellationToken);

    public async Task<Page?> GetHomepageAsync(
        [Parent] Space space,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        if (space.HomepageId is null)
        {
            return null;
        }

        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        // A restricted homepage is null here exactly like a nonexistent one (design.md
        // §6.7), but the denial itself is audited with its failing restriction (§7)
        // before the collapse - same split as Query.page.
        var result = await readService.GetPageAsync(space.HomepageId.Value, principal, cancellationToken);
        if (result is ReadResult<Page>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "page.view", AuditSubjectType.Page, space.HomepageId.Value, denied.Reason, cancellationToken);
        }

        return result.ValueOrNull();
    }

    /// <summary>
    /// design.md §8: "grants: [SpaceGrant!]! # space-admin only". Absent (empty), not
    /// forbidden, for anyone who is neither instance-admin nor this space's own
    /// space-admin — the same convention every other read in this schema uses (design.md
    /// §6.7), even though a rule list doesn't carry the same existence-leak risk a
    /// restricted Page does; consistency with the rest of the schema matters more than a
    /// one-off exception here.
    /// </summary>
    public async Task<IReadOnlyList<AccessRule>> GetGrantsAsync(
        [Parent] Space space,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);

        var isSpaceAdmin = EffectivePermissionCalculator.ComputeSpaceRole(grants, principal) == SpaceRole.SpaceAdmin;
        if (!instanceRoleAccessor.IsInstanceAdmin && !isSpaceAdmin)
        {
            return [];
        }

        return grants;
    }

    /// <summary>
    /// Space-scoped trash listing — no query for this exists in design.md's abbreviated
    /// schema sketch, so this shape is this round's own call, gated on space-role
    /// Editor+ (the same minimum <c>RestorePageAsync</c> itself requires — canEdit needs
    /// Editor+, design.md §6.4), not a full per-page canView/canEdit computation.
    ///
    /// Known, documented limitation: a page-level <c>PageRestriction</c> attached
    /// directly to a trashed page is NOT filtered out here, unlike every other
    /// Page-returning field in this schema. Restoring it will still correctly refuse via
    /// <c>RestorePageAsync</c>'s own per-page canEdit check regardless of what this
    /// listing showed, so nothing actually restorable leaks a restore capability that
    /// shouldn't exist — but an editor could see the title/existence of a restricted
    /// trashed page they technically shouldn't. Flagged rather than silently accepted;
    /// the clean fix is a real <c>IPageReadService</c> trash method that reuses the same
    /// restriction-accumulation logic live pages get, which is a Core-side change not
    /// requested this round.
    /// </summary>
    public async Task<IReadOnlyList<Page>> GetTrashedPagesAsync(
        [Parent] Space space,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);
        var role = EffectivePermissionCalculator.ComputeSpaceRole(grants, principal);
        if (!instanceRoleAccessor.IsInstanceAdmin && (role is null || role.Value < SpaceRole.Editor))
        {
            return [];
        }

        return await db.Pages.IgnoreQueryFilters()
            .Where(p => p.SpaceId == space.Id && p.IsDeleted)
            .OrderByDescending(p => p.DeletedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
