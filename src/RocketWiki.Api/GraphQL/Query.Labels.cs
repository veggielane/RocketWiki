using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// Known label names for the search facet and label-filter UI (the SPA's
    /// SearchFacets operation) — from spaces the caller holds a role in, only.
    ///
    /// Granularity is deliberate and worth stating: label *names* are space-level
    /// taxonomy (created by editors, attached to nothing in particular), gated the
    /// same way space names and the space list are — on holding any role in the
    /// space. They are not page data: which pages carry a label is the
    /// restriction-sensitive question, and that always goes through the
    /// canView-filtered read paths (ILabelService.GetPagesByLabelAsync, search's
    /// label facet), never through this list. An unknown or inaccessible spaceKey
    /// yields an empty list, indistinguishable from a space with no labels
    /// (design.md §6.7: absent, never forbidden).
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<string>> Labels(
        string? spaceKey,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var viewableSpaceIds = await GetViewableSpaceIdsAsync(db, principal, spaceKey, cancellationToken);
        if (viewableSpaceIds.Count == 0)
        {
            return [];
        }

        return await db.Labels
            .Where(l => viewableSpaceIds.Contains(l.SpaceId))
            .Select(l => l.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The id-carrying twin of <see cref="Labels"/>, added because the label mutations
    /// (attachLabel/detachLabel) take label ids while every read path returned names
    /// only — no edit round-trip was possible (design.md §8's known-deltas list; the
    /// SPA's LabelEditor maps names to ids through this). Additive on purpose:
    /// <c>labels</c> keeps its shipped `[String!]!` shape for the operations the SPA
    /// already runs. Same gate, same granularity reasoning, and the same
    /// absent-not-forbidden collapse as <see cref="Labels"/> — a label id is the same
    /// space-level taxonomy datum its name is, just addressable.
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<LabelRef>> LabelDetails(
        string? spaceKey,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var viewableSpaceIds = await GetViewableSpaceIdsAsync(db, principal, spaceKey, cancellationToken);
        if (viewableSpaceIds.Count == 0)
        {
            return [];
        }

        return await db.Labels
            .Where(l => viewableSpaceIds.Contains(l.SpaceId))
            .OrderBy(l => l.Name)
            .ThenBy(l => l.Id)
            .Select(l => new LabelRef(l.Id, l.SpaceId, l.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The shared "which of these spaces does this principal hold any role
    /// in" filter both label listings gate on (design.md §6.4's space view gate).</summary>
    private static async Task<IReadOnlyList<Guid>> GetViewableSpaceIdsAsync(
        RocketWikiDbContext db, Principal principal, string? spaceKey, CancellationToken cancellationToken)
    {
        var spaces = await db.Spaces
            .Where(s => spaceKey == null || s.Key == spaceKey)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (spaces.Count == 0)
        {
            return [];
        }

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId != null && spaces.Contains(r.SpaceId.Value))
            .ToListAsync(cancellationToken);

        return spaces
            .Where(id => EffectivePermissionCalculator.ComputeSpaceRole(
                grants.Where(g => g.SpaceId == id), principal) is not null)
            .ToList();
    }
}
