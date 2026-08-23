using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// design.md §6.7's object-level authorization, applied to every non-root path a
/// <c>Page</c> is reachable by. Every Page-returning method here calls
/// <c>IPageReadService</c> — never a raw EF query, never the entity's own navigation
/// collections — so a restricted page is exactly as unreachable via
/// `parent`/`children`/`revisions` as it is via the root `page(id)` query.
/// <see cref="PageType"/> ignores those raw navigations for the same reason: nothing
/// here is a shortcut around the service.
///
/// Comments/attachments/labels are different: they carry no restriction of their
/// own, only the parent Page's (design.md's model has no per-comment or
/// per-attachment access rule), so once the parent Page has already passed canView
/// to be resolved as a GraphQL object at all, reading its children rows directly is
/// safe - there's no second gate to route through a service for.
/// </summary>
public sealed class PageFieldResolvers
{
    [AuditAction("page.view")]
    [UseAuditDispatch]
    public string GetContent([Parent] Page page) => page.CurrentContent;

    public async Task<Page?> GetParentAsync(
        [Parent] Page page,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        if (page.ParentPageId is null)
        {
            return null;
        }

        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        // Same canView gate as the root query, for the parent id specifically - a
        // restricted parent is absent here exactly like it would be at the root
        // (design.md §6.7), not a distinguishable "forbidden".
        return await readService.GetPageAsync(page.ParentPageId.Value, principal, cancellationToken);
    }

    public async Task<IReadOnlyList<Page>> GetChildrenAsync(
        [Parent] Page page,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        PageByIdDataLoader pageByIdLoader,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        // IPageReadService has no direct "children of this page" method; the
        // permission-safe source of child ids is the already-pruned space tree
        // (design.md §6.7 - a hidden child is dropped there, never handed back here
        // to filter ourselves). Materializing each child's full Page goes through
        // PageByIdDataLoader, which dedupes/parallelizes across one request - see its
        // own doc for what it does and doesn't optimize away.
        var tree = await readService.GetPageTreeAsync(page.SpaceId, principal, cancellationToken);
        var node = FindNode(tree, page.Id);
        if (node is null || node.Children.Count == 0)
        {
            return [];
        }

        var childIds = node.Children.Select(c => c.Id).ToArray();
        var loaded = await pageByIdLoader.LoadRequiredAsync(childIds, cancellationToken);
        return loaded;
    }

    [AuditAction("page.view")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<PageRevision>> GetRevisionsAsync(
        [Parent] Page page,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var revisions = await readService.GetRevisionHistoryAsync(page.Id, principal, cancellationToken);
        return revisions ?? [];
    }

    public async Task<IReadOnlyList<Comment>> GetCommentsAsync(
        [Parent] Page page, [Service] RocketWikiDbContext db, CancellationToken cancellationToken) =>
        await db.Comments
            .Where(c => c.PageId == page.Id)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Attachment>> GetAttachmentsAsync(
        [Parent] Page page, [Service] RocketWikiDbContext db, CancellationToken cancellationToken) =>
        await db.Attachments
            .Where(a => a.PageId == page.Id)
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    /// <summary>Batched via <see cref="SpaceKeyBySpaceIdDataLoader"/> (see its doc for
    /// why this is metadata resolution, not an authorization decision). A missing key
    /// would mean a Page row pointing at no Space row — an FK-impossible state worth
    /// crashing on, not defaulting away.</summary>
    public async Task<string> GetSpaceKeyAsync(
        [Parent] Page page, SpaceKeyBySpaceIdDataLoader spaceKeyLoader, CancellationToken cancellationToken) =>
        await spaceKeyLoader.LoadAsync(page.SpaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Page {page.Id} references space {page.SpaceId}, which does not exist.");

    public async Task<IReadOnlyList<string>> GetLabelsAsync(
        [Parent] Page page, [Service] RocketWikiDbContext db, CancellationToken cancellationToken) =>
        await db.PageLabels
            .Where(pl => pl.PageId == page.Id)
            .Select(pl => pl.Label!.Name)
            .ToListAsync(cancellationToken);

    private static PageTreeNode? FindNode(IReadOnlyList<PageTreeNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
            {
                return node;
            }

            var found = FindNode(node.Children, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
