using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.Reads;

/// <summary>
/// The one copy of "which spaces can this principal see" at the API layer, shared by
/// the GraphQL resolvers (<c>Query.Spaces</c>/<c>Query.Space</c>) and the MCP tools
/// (<c>WikiMcpTools.list_spaces</c>/<c>get_page_tree</c>). design.md §8's MCP section
/// requires "same code path... no parallel, subtly-different read path to keep honest";
/// there is no Core-side space *read* service yet (unlike <c>IPageReadService</c> for
/// pages), so until one exists this class is that single path — extracted from
/// Query.Spaces.cs rather than duplicated into the MCP tools.
///
/// A space's own view gate is just "holds any SpaceGrant role" — no
/// restriction-accumulation the way a Page has (design.md §6.4) — computed via
/// <see cref="EffectivePermissionCalculator"/> per space. Archived spaces are excluded
/// by the normal EF query filter; whether their previous viewers should still see them
/// is design.md §6.5.1's stated open question, so exclusion is the conservative default.
/// </summary>
internal static class SpaceReads
{
    /// <summary>design.md §8 schema sketch: "only spaces the caller can view".</summary>
    public static async Task<IReadOnlyList<Space>> GetViewableSpacesAsync(
        RocketWikiDbContext db, Principal principal, CancellationToken cancellationToken)
    {
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
    public static async Task<Space?> GetViewableSpaceByKeyAsync(
        RocketWikiDbContext db, Principal principal, string key, CancellationToken cancellationToken)
    {
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
