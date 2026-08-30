using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
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
    /// <summary>
    /// design.md §8 schema sketch: "only spaces the caller can view". A list-shaped
    /// read stays a plain filtered list on purpose — per ReadDenialAudit's doc (and
    /// the tree-pruning contract in IPageReadService), a listing the caller was
    /// allowed to make is not a denial of each absent item, so nothing here reports
    /// (or audits) per-space visibility failures. Only the specific-space lookup
    /// below distinguishes a denial.
    /// </summary>
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

    /// <summary>
    /// The specific-space lookup, returning the same internal not-found-vs-denied
    /// split the Page read services return (design.md §6.7 / <c>ReadResult</c>'s doc):
    /// a directly requested subject that exists but fails visibility is a denial the
    /// audit log must record (§7), which a bare null could never carry. Both callers
    /// (Query.Space, WikiMcpTools.get_page_tree) audit the Denied case via
    /// ReadDenialAudit and then collapse it to the exact response NotFound gets —
    /// the distinction dies at the response edge, never on the wire. NotFound also
    /// covers an archived space (the EF query filter removes it before this method
    /// can tell), which per §7's outcome vocabulary audits nothing: no access
    /// decision exists for a subject that isn't there.
    /// </summary>
    public static async Task<SpaceReadResult> GetViewableSpaceByKeyAsync(
        RocketWikiDbContext db, Principal principal, string key, CancellationToken cancellationToken)
    {
        // Canonicalized so /spaces/eng and /spaces/ENG name the same space (see
        // SpaceKeys.Canonical). This is the one copy of "look a space up by key" that
        // both GraphQL and MCP go through, which is why the normalization belongs here
        // rather than in either caller.
        var canonicalKey = SpaceKeys.Canonical(key);
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Key == canonicalKey, cancellationToken);
        if (space is null)
        {
            return new SpaceReadResult.NotFound();
        }

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);
        return EffectivePermissionCalculator.ComputeSpaceRole(grants, principal) is null
            ? new SpaceReadResult.Denied(space.Id, "no-space-role")
            : new SpaceReadResult.Found(space);
    }
}

/// <summary>
/// Space-flavored twin of Core's <c>ReadResult&lt;T&gt;</c>, local to the API layer
/// because that's where SpaceReads itself lives (no Core space read service yet — see
/// the class doc above). It exists rather than reusing <c>ReadResult&lt;Space&gt;</c>
/// because a denial here must carry the space's id for the audit row's subject —
/// the caller only holds a key, and handing back the entity on a denial would put a
/// forbidden object in the caller's hands.
/// </summary>
internal abstract record SpaceReadResult
{
    private SpaceReadResult()
    {
    }

    internal sealed record Found(Space Space) : SpaceReadResult;

    /// <summary>No such (unarchived) space. No access decision was made — nothing existed to decide about.</summary>
    internal sealed record NotFound : SpaceReadResult;

    /// <summary>The space exists but the principal holds no role in it.
    /// <paramref name="Reason"/> is always <c>no-space-role</c> today; kept explicit
    /// so the audit row records what the rule engine computed, not what a caller
    /// assumed (design.md §7).</summary>
    internal sealed record Denied(Guid SpaceId, string Reason) : SpaceReadResult;
}
