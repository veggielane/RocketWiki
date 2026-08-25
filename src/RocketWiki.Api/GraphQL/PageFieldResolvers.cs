using HotChocolate;
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
        [Service] IAuditSink auditSink,
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
        // (design.md §6.7), not a distinguishable "forbidden". A Denied here should be
        // unreachable in practice - view restrictions accumulate downward (§6.4), so a
        // denied parent implies this child was denied too and never resolved - but if a
        // rule change lands mid-request it's still a real denial, audited like any
        // other (§7) before collapsing to null.
        var result = await readService.GetPageAsync(page.ParentPageId.Value, principal, cancellationToken);
        if (result is ReadResult<Page>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "page.view", AuditSubjectType.Page, page.ParentPageId.Value, denied.Reason, cancellationToken);
        }

        return result.ValueOrNull();
    }

    public async Task<IReadOnlyList<Page>> GetChildrenAsync(
        [Parent] Page page,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
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
        //
        // A Denied tree here means the caller lost their space role between this page
        // resolving (which required canView, hence a role) and now - race-only, but a
        // real denial of a real browse, so audited (§7) then collapsed to the same []
        // as "no children". Individual children pruned from a Found tree are not
        // denials - see IPageReadService.GetPageTreeAsync's doc.
        var treeResult = await readService.GetPageTreeAsync(page.SpaceId, principal, cancellationToken);
        if (treeResult is ReadResult<IReadOnlyList<PageTreeNode>>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "space.browse", AuditSubjectType.Space, page.SpaceId, denied.Reason, cancellationToken);
            return [];
        }

        var tree = treeResult.ValueOrNull() ?? [];
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
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        // Race-only, like GetParentAsync: this Page already passed canView to resolve
        // at all, and revisions require nothing beyond canView on the same page - a
        // Denied means a rule change landed mid-request. Still audited (§7); DbAuditSink
        // then suppresses AuditFieldMiddleware's would-be Success row for this subject.
        var result = await readService.GetRevisionHistoryAsync(page.Id, principal, cancellationToken);
        if (result is ReadResult<IReadOnlyList<PageRevision>>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "page.view", AuditSubjectType.Page, page.Id, denied.Reason, cancellationToken);
        }

        return result.ValueOrNull() ?? [];
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

    /// <summary>Batched via <see cref="LabelRefsByPageIdDataLoader"/> (previously a
    /// direct per-page query — an N+1 when a query resolved labels across children).</summary>
    public async Task<IReadOnlyList<string>> GetLabelsAsync(
        [Parent] Page page, LabelRefsByPageIdDataLoader labelLoader, CancellationToken cancellationToken)
    {
        var labels = await labelLoader.LoadAsync(page.Id, cancellationToken);
        return (labels ?? []).Select(l => l.Name).ToList();
    }

    /// <summary>The id-carrying twin of <see cref="GetLabelsAsync"/>, so the SPA's
    /// label editor can round-trip attach/detach by id (see Query.labelDetails' doc
    /// for why this is additive next to the names-only field). Same loader, so
    /// selecting both fields still costs one PageLabels query.</summary>
    public async Task<IReadOnlyList<LabelRef>> GetLabelDetailsAsync(
        [Parent] Page page, LabelRefsByPageIdDataLoader labelLoader, CancellationToken cancellationToken) =>
        await labelLoader.LoadAsync(page.Id, cancellationToken) ?? [];

    /// <summary>
    /// This page's key/value properties (design.md §20), batched via
    /// <see cref="PagePropertyValuesByPageIdDataLoader"/> and already ordered by the
    /// registry's SortOrder then key. No audit row of its own and no gate of its own:
    /// a nested field inside an already-audited page read, and properties carry no
    /// restriction beyond the page's (§6.4.2's rule, extended to properties in §20).
    /// </summary>
    public async Task<IReadOnlyList<PagePropertyValue>> GetPropertiesAsync(
        [Parent] Page page, PagePropertyValuesByPageIdDataLoader propertyLoader, CancellationToken cancellationToken) =>
        await propertyLoader.LoadAsync(page.Id, cancellationToken) ?? [];

    /// <summary>
    /// The page's protective marking (design.md §21). Visible to anyone who can view the
    /// page — which, since §21, means anyone the marking itself already admitted; showing
    /// a cleared reader why they were cleared is not a leak, and a page whose banner
    /// omitted its own classification would be worse than useless to an author deciding
    /// what to write in it. Batched through a DataLoader so a list of pages costs one
    /// query.
    /// </summary>
    public async Task<PageMarkingView> GetMarkingAsync(
        [Parent] Page page, PageMarkingByPageIdDataLoader markingLoader, CancellationToken cancellationToken) =>
        // The loader fills every requested key, so the null branch is unreachable - but a
        // NonNull GraphQL field must not be able to throw a nullability surprise, and the
        // one honest fallback for "no marking" is the same TOP SECRET every other read
        // path substitutes (design.md §21), never a blank badge.
        PageMarkingView.From(
            await markingLoader.LoadAsync(page.Id, cancellationToken) ?? ProtectiveMarking.FailClosed);

    /// <summary>See <see cref="ViewerWatchesPageDataLoader"/> for the viewer-relative
    /// contract and why this emits no audit row of its own (the caller's own
    /// subscription-bookkeeping, inside an already-audited page read - design.md §7).</summary>
    public async Task<bool> GetViewerIsWatchingAsync(
        [Parent] Page page, ViewerWatchesPageDataLoader watchLoader, CancellationToken cancellationToken) =>
        await watchLoader.LoadAsync(page.Id, cancellationToken);

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
