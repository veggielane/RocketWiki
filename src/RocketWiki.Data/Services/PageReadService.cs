using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IPageReadService (design.md §6.7). Lives in
/// RocketWiki.Data for the same reason PageService does: it needs RocketWikiDbContext
/// directly, and Data already references Core, so an EF-dependent service can't live in
/// Core without a circular project reference.
/// </summary>
public class PageReadService : IPageReadService
{
    private readonly RocketWikiDbContext _db;

    public PageReadService(RocketWikiDbContext db)
    {
        _db = db;
    }

    public async Task<Page?> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return null;
        }

        var canView = await ComputeCanViewAsync(space, page, principal, cancellationToken);
        return canView ? page : null;
    }

    public async Task<IReadOnlyList<PageRevision>?> GetRevisionHistoryAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        // Revision history needs nothing beyond the exact same canView gate as the page
        // itself (design.md §6.7) - reusing GetPageAsync keeps that a single source of truth.
        var page = await GetPageAsync(pageId, principal, cancellationToken);
        if (page is null)
        {
            return null;
        }

        return await _db.PageRevisions
            .Where(r => r.PageId == pageId)
            .OrderByDescending(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PageTreeNode>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken);
        if (space is null)
        {
            return Array.Empty<PageTreeNode>();
        }

        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == spaceId)
            .ToListAsync(cancellationToken);

        // No space role at all means no view of anything in it - an empty tree, not an
        // error, matching "invisible, not merely unopenable" (design.md §6.7).
        if (EffectivePermissionCalculator.ComputeSpaceRole(spaceGrants, principal) is null)
        {
            return Array.Empty<PageTreeNode>();
        }

        // Two queries total regardless of tree depth or size: every live page in the
        // space, and every view-restriction attached to any of them. The walk below is
        // then pure in-memory recursion - exactly what AncestorPath exists to make cheap.
        var pages = await _db.Pages.Where(p => p.SpaceId == spaceId).ToListAsync(cancellationToken);
        var pageIds = pages.Select(p => p.Id).ToArray();
        var restrictions = pageIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.Action == PageAction.View && r.PageId != null && pageIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        var restrictionsByPageId = restrictions
            .GroupBy(r => r.PageId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var childrenByParentId = pages
            .Where(p => p.ParentPageId is not null)
            .GroupBy(p => p.ParentPageId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.SortOrder).ToList());
        var rootPages = pages.Where(p => p.ParentPageId is null).OrderBy(p => p.SortOrder);

        var result = new List<PageTreeNode>();
        foreach (var root in rootPages)
        {
            var node = BuildNodeIfVisible(root, childrenByParentId, restrictionsByPageId, principal);
            if (node is not null)
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns null - pruning this page and its entire subtree - the moment this page's
    /// own view restrictions fail. Never re-checks ancestor restrictions: a child is
    /// only ever visited after its parent already passed, so "carried down the
    /// recursion" (design.md §6.7) means each node costs one restriction-list lookup,
    /// not a walk back up the tree.
    /// </summary>
    private static PageTreeNode? BuildNodeIfVisible(
        Page page,
        IReadOnlyDictionary<Guid, List<Page>> childrenByParentId,
        IReadOnlyDictionary<Guid, List<AccessRule>> restrictionsByPageId,
        Principal principal)
    {
        if (restrictionsByPageId.TryGetValue(page.Id, out var ownRestrictions))
        {
            foreach (var rule in ownRestrictions)
            {
                if (!AccessRuleExpression.Evaluate(rule.ExpressionJson, principal).IsMatch)
                {
                    return null;
                }
            }
        }

        var children = new List<PageTreeNode>();
        if (childrenByParentId.TryGetValue(page.Id, out var childPages))
        {
            foreach (var child in childPages)
            {
                var childNode = BuildNodeIfVisible(child, childrenByParentId, restrictionsByPageId, principal);
                if (childNode is not null)
                {
                    children.Add(childNode);
                }
            }
        }

        return new PageTreeNode(page.Id, page.Title, page.Slug, page.SortOrder, children);
    }

    private async Task<bool> ComputeCanViewAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);

        var restrictionIds = page.GetAncestorIds().Append(page.Id).ToArray();
        var restrictions = restrictionIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && restrictionIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        // Replica status is irrelevant to canView (design.md §6.4: it only ever affects
        // canEdit), so this is always false here regardless of the space's origin.
        var permission = EffectivePermissionCalculator.Compute(spaceGrants, restrictions, isReplicaSpace: false, principal);
        return permission.CanView;
    }
}
