using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Content;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The target of one <c>page://</c> link in a page's content (design.md §6.7 / §21.8).
/// <see cref="Id"/> echoes the id as written — the caller already holds it, in Markdown
/// they can read. Then exactly one of: <see cref="Page"/> (viewable), <see cref="Denial"/>
/// (a placeholder with its marking and reasons), or neither (no such live page).
/// </summary>
public sealed record PageLinkTarget(Guid Id, Page? Page, AccessDenialView? Denial);

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
///
/// <para><b>Two fields disclose rather than omit</b> (§21.8): <c>parentDenial</c> and
/// <c>linkTargets</c> render a page the caller cannot view as a placeholder carrying its
/// marking and the gates they failed. Both resolve through
/// <see cref="PageAccessByIdDataLoader"/>, and both are nested under a Page the caller
/// already resolved — a denied parent or link target is disclosed only to someone who
/// can already read the page that names it.</para>
/// </summary>
public sealed class PageFieldResolvers
{
    [AuditAction("page.view")]
    [UseAuditDispatch]
    public string GetContent([Parent] Page page) => page.CurrentContent;

    /// <summary>
    /// The parent, when the caller may view it; null otherwise (design.md §6.7) — a
    /// restricted parent is absent here exactly like it would be at the root, not a
    /// distinguishable "forbidden". A denied parent is a real case, not a race: markings
    /// do not accumulate (§21.5), so a viewable OFFICIAL child under a SECRET parent is
    /// ordinary, and the denial is audited (§7) before collapsing to null.
    /// <c>parentDenial</c> beside this field is where the placeholder is disclosed; both
    /// share one loader call, so selecting the two costs one decision and one audit row.
    /// </summary>
    public async Task<Page?> GetParentAsync(
        [Parent] Page page,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        PageAccessByIdDataLoader accessLoader,
        CancellationToken cancellationToken)
    {
        if (page.ParentPageId is null || principalAccessor.Current is null)
        {
            return null;
        }

        var access = await accessLoader.LoadAsync(page.ParentPageId.Value, cancellationToken);
        if (access is PageAccess.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "page.view", AuditSubjectType.Page, page.ParentPageId.Value, denied.Denial.Reason, cancellationToken);
        }

        return (access as PageAccess.Found)?.Page;
    }

    /// <summary>
    /// The placeholder for a parent the caller cannot view (design.md §6.7 / §21.8):
    /// non-null exactly when the parent exists and is denied, so a breadcrumb can show
    /// <c>(protected)</c> with the parent's marking instead of a gap. Null when there is
    /// no parent, when the parent is viewable (select <c>parent</c>), and when it does not
    /// exist. The same loader and the same audit row as <c>parent</c>: a denial here is
    /// a directly requested subject and is recorded, once per request, whichever of the
    /// two fields asked.
    /// </summary>
    public async Task<AccessDenialView?> GetParentDenialAsync(
        [Parent] Page page,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        [Service] SelectorCatalog catalog,
        PageAccessByIdDataLoader accessLoader,
        CancellationToken cancellationToken)
    {
        if (page.ParentPageId is null || principalAccessor.Current is null)
        {
            return null;
        }

        var access = await accessLoader.LoadAsync(page.ParentPageId.Value, cancellationToken);
        if (access is not PageAccess.Denied denied)
        {
            return null;
        }

        await ReadDenialAudit.RecordAsync(
            auditSink, "page.view", AuditSubjectType.Page, page.ParentPageId.Value, denied.Denial.Reason, cancellationToken);
        return AccessDenialView.From(denied.Denial, catalog, page.ParentPageId.Value);
    }

    /// <summary>
    /// The targets of every <c>page://</c> link in this page's current content, in
    /// first-occurrence order, one entry per distinct id (design.md §6.7 / §21.8). A
    /// viewable target is a Page; a denied one is a placeholder with its marking and
    /// reasons; a missing one has neither.
    ///
    /// <para><b>Why a nested field and not a root field taking ids.</b> The id set is
    /// derived server-side from content the caller has already been permitted to read
    /// (<see cref="PageLinkScanner"/>), so a caller learns the existence and marking only
    /// of pages an author already linked from something they can see — never of an
    /// arbitrary id they obtained elsewhere. Batched through
    /// <see cref="PageAccessByIdDataLoader"/>; no per-target audit row, on the same rule as
    /// <c>children</c> and <c>pageSubtree</c>: this is a listing continuation of the
    /// already-audited read of the containing page, not a directly requested subject.</para>
    /// </summary>
    public async Task<IReadOnlyList<PageLinkTarget>> GetLinkTargetsAsync(
        [Parent] Page page,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] SelectorCatalog catalog,
        PageAccessByIdDataLoader accessLoader,
        CancellationToken cancellationToken)
    {
        if (principalAccessor.Current is null)
        {
            return [];
        }

        var ids = PageLinkScanner.Extract(page.CurrentContent);
        if (ids.Count == 0)
        {
            return [];
        }

        var results = await accessLoader.LoadAsync(ids.ToArray(), cancellationToken);
        var targets = new List<PageLinkTarget>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            targets.Add(results[i] switch
            {
                PageAccess.Found found => new PageLinkTarget(ids[i], found.Page, null),
                PageAccess.Denied denied => new PageLinkTarget(ids[i], null, AccessDenialView.From(denied.Denial, catalog, ids[i])),
                // NotFound, or a null the loader gave an unresolved key: no such page.
                _ => new PageLinkTarget(ids[i], null, null),
            });
        }

        return targets;
    }

    /// <summary>
    /// This page's directly-visible children, in tree order.
    ///
    /// <para>Resolved through <see cref="VisibleChildIdsByPageIdDataLoader"/> over
    /// <c>IPageReadService.GetVisibleChildIdsAsync</c>, which gates each child on its OWN
    /// chain and marking. It used to locate this page inside the already-pruned space
    /// tree instead, and that had two costs. It was wrong: a page whose ancestor sits
    /// above the caller's clearance is pruned from the tree along with its whole subtree
    /// (§21.5), so this page was absent from the tree and answered <c>children</c> with
    /// an empty list — even though the design says such a page "stays reachable by id",
    /// and its children were perfectly viewable. And it was expensive: the tree walk
    /// materializes every page in the space, unmemoized, once per parent being resolved.
    /// Both are the same fix.</para>
    ///
    /// <para>No denial audit here, and none is missing: a pruned listing is not a refused
    /// request (IPageReadService.GetPageTreeAsync's contract says so for the tree, and
    /// this is the same rule), and reaching this resolver at all means this Page already
    /// passed canView and was audited where it was read. And no placeholders (§21.8):
    /// <c>children</c> is an omitting surface; the tree is where a denied child is
    /// disclosed.</para>
    /// </summary>
    public async Task<IReadOnlyList<Page>> GetChildrenAsync(
        [Parent] Page page,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        VisibleChildIdsByPageIdDataLoader childIdsLoader,
        PageByIdDataLoader pageByIdLoader,
        CancellationToken cancellationToken)
    {
        if (principalAccessor.Current is null)
        {
            return [];
        }

        var childIds = await childIdsLoader.LoadAsync(page.Id, cancellationToken);
        if (childIds is not { Count: > 0 })
        {
            return [];
        }

        // Materializing each child's full Page goes through PageByIdDataLoader, which
        // dedupes/parallelizes across one request - see its own doc for what it does and
        // doesn't optimize away.
        //
        // LoadAsync + drop the nulls, NOT LoadRequiredAsync. The two loads are
        // separate: the child-id loader answers with what was visible when IT ran, and
        // the page loader deliberately omits keys that fail canView when it runs. A
        // restriction landing between them therefore leaves an id with no page — and
        // LoadRequiredAsync turns that into a GraphQL error, which is both a worse
        // answer than the silent absence every other read path gives (§6.7) and, being
        // observably different from "no such child", a way to learn the page exists.
        var children = await pageByIdLoader.LoadAsync(childIds.ToArray(), cancellationToken);
        return [.. children.Where(child => child is not null).Select(child => child!)];
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

        // Race-only, like a denied parent used to be: this Page already passed canView
        // to resolve at all, and revisions require nothing beyond canView on the same
        // page - a Denied means a rule change landed mid-request. Still audited (§7);
        // DbAuditSink then suppresses AuditFieldMiddleware's would-be Success row for
        // this subject.
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
        [Parent] Page page,
        PageMarkingByPageIdDataLoader markingLoader,
        [Service] SelectorCatalog catalog,
        CancellationToken cancellationToken) =>
        // The loader fills every requested key, so the null branch is unreachable - but a
        // NonNull GraphQL field must not be able to throw a nullability surprise, and the
        // one honest fallback for "no marking" is the same TOP SECRET every other read
        // path substitutes (design.md §21), never a blank badge.
        PageMarkingView.From(
            await markingLoader.LoadAsync(page.Id, cancellationToken) ?? ProtectiveMarking.FailClosed, catalog);

    /// <summary>See <see cref="ViewerWatchesPageDataLoader"/> for the viewer-relative
    /// contract and why this emits no audit row of its own (the caller's own
    /// subscription-bookkeeping, inside an already-audited page read - design.md §7).</summary>
    public async Task<bool> GetViewerIsWatchingAsync(
        [Parent] Page page, ViewerWatchesPageDataLoader watchLoader, CancellationToken cancellationToken) =>
        await watchLoader.LoadAsync(page.Id, cancellationToken);

}
