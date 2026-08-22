using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// design.md §8 schema sketch: "spaces: [Space!]! # only spaces the caller can
    /// view". A space's own view gate is just "holds any SpaceGrant role" — no
    /// restriction-accumulation the way a Page has (design.md §6.4) — computed
    /// directly via <see cref="EffectivePermissionCalculator"/> per space; see
    /// <see cref="SpaceFieldResolvers"/>'s own doc for why calling it straight from
    /// this resolver layer is the expected pattern here, not a Page-style shortcut.
    /// Archived spaces are excluded via the normal EF query filter (no
    /// IgnoreQueryFilters here) — whether editors/viewers should still see an
    /// archived space at all is design.md §6.5.1's own stated open question, so
    /// excluding it is the conservative default until that's resolved.
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<Space>> Spaces(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var spaces = await db.Spaces.ToListAsync(cancellationToken);
        var allGrants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant)
            .ToListAsync(cancellationToken);

        return spaces
            .Where(s => EffectivePermissionCalculator.ComputeSpaceRole(
                allGrants.Where(g => g.SpaceId == s.Id), principal) is not null)
            .ToList();
    }

    /// <summary>Same "absent, not forbidden" convention as Page (design.md §6.7): null
    /// for a space that doesn't exist, is archived, or the caller holds no role in —
    /// the three are not distinguished from one another.</summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<Space?> Space(
        string key,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (space is null)
        {
            return null;
        }

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);
        return EffectivePermissionCalculator.ComputeSpaceRole(grants, principal) is null ? null : space;
    }
}
