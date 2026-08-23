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
