using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Core.Telemetry;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IPageReadService (design.md §6.7). Lives in
/// RocketWiki.Data for the same reason PageService does: it needs RocketWikiDbContext
/// directly, and Data already references Core, so an EF-dependent service can't live in
/// Core without a circular project reference.
///
/// Denial auditing deliberately does NOT happen here (design.md §6.7's split): this
/// service returns the internal not-found-vs-denied ReadResult, and the API layer -
/// which owns IAuditSink, the request-scoped dedup, and the channel context - audits
/// the Denied case before collapsing it to null. Auditing from inside this service
/// would drag the Api project's audit seam into Core/Data (a circular reference) and
/// would bypass the per-request deduplication design.md §8 requires.
/// </summary>
public class PageReadService : IPageReadService
{
    private readonly RocketWikiDbContext _db;
    private readonly PermissionContextLoader _permissions;

    public PageReadService(RocketWikiDbContext db)
    {
        _db = db;
        _permissions = new PermissionContextLoader(db);
    }

    /// <inheritdoc />
    public async Task<Guid?> FindPageIdBySlugAsync(
        string spaceKey, string slug, CancellationToken cancellationToken = default)
    {
        // Both halves of the address are canonicalized before they meet the stored
        // (canonical) values, which is what makes /spaces/ENG/My-Page and
        // /spaces/eng/my-page resolve the same page. Normalizing HERE rather than in the
        // resolver is deliberate: a new entry point - another resolver, an MCP tool, a
        // future REST route - reaches a page by slug only through this method, so it
        // inherits the rule, whereas an edge-level helper is exactly what a new entry
        // point forgets. Storage is canonical too (RocketWikiDbContext), without which
        // normalizing here would make the lookup ambiguous rather than case-insensitive.
        var canonicalSlug = PageSlugs.Canonical(slug);
        var canonicalSpaceKey = SpaceKeys.Canonical(spaceKey);

        // No permission filtering here by design — see the interface. The global query
        // filters still apply, so an archived space's pages are not addressable, and
        // IsDeleted keeps a trashed page's slug from resolving while the row survives
        // for restore.
        return await _db.Pages
            .Where(p => !p.IsDeleted && p.Slug == canonicalSlug && p.Space!.Key == canonicalSpaceKey)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ReadResult<Page>> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return new ReadResult<Page>.NotFound();
        }

        // Fail closed on an unresolvable space even though the permission computation
        // below no longer needs the row: no space, no grants to hold access under.
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new ReadResult<Page>.NotFound();
        }

        var permission = await ComputePermissionAsync(page, principal, cancellationToken);
        return permission.CanView
            ? new ReadResult<Page>.Found(page)
            : new ReadResult<Page>.Denied(permission.ViewDenialReason ?? EffectivePermissionCalculator.NoSpaceAccessReason);
    }

    /// <inheritdoc />
    public async Task<PageAccess> GetPageAccessAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return new PageAccess.NotFound();
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new PageAccess.NotFound();
        }

        // Replica status is irrelevant to canView (design.md §6.4), exactly as on GetPageAsync.
        var context = await _permissions.LoadAsync(page, isReplicaSpace: false, cancellationToken);
        return Decide(page, context, principal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, PageAccess>> GetPageAccessBatchAsync(
        IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default)
    {
        var ids = pageIds.Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => (PageAccess)new PageAccess.NotFound());
        if (ids.Length == 0)
        {
            return result;
        }

        // Four queries for the whole batch regardless of its size, the same shape as
        // GetPagesAsync: the pages, then the loader's three.
        var pages = await _db.Pages.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        if (pages.Count == 0)
        {
            return result;
        }

        var batch = await _permissions.LoadBatchAsync(
            pages.Select(PermissionSubject.For).ToList(), cancellationToken);
        foreach (var page in pages)
        {
            result[page.Id] = Decide(page, batch.For(PermissionSubject.For(page), isReplicaSpace: false), principal);
        }

        return result;
    }

    /// <summary>
    /// The disclosed verdict for one page. The enforcement form decides — it IS the gate,
    /// and it feeds the permission-check histogram for this read like any other (§15) —
    /// and only a denial runs the inspector form on top, so the placeholder can name every
    /// failing gate rather than the first (§6.7). The two agree by construction (one
    /// ladder); the withholding rule for a caller without space access is applied inside
    /// <see cref="PageDenial.From"/>, never here.
    /// </summary>
    private static PageAccess Decide(Page page, PagePermissionContext context, Principal principal)
    {
        if (context.Compute(principal).CanView)
        {
            return new PageAccess.Found(page);
        }

        return new PageAccess.Denied(PageDenial.From(context.Explain(principal), context.Marking));
    }

    public async Task<ReadResult<IReadOnlyList<PageRevision>>> GetRevisionHistoryAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        // Revision history needs nothing beyond the exact same canView gate as the page
        // itself (design.md §6.7) - reusing GetPageAsync keeps that a single source of truth.
        switch (await GetPageAsync(pageId, principal, cancellationToken))
        {
            case ReadResult<Page>.NotFound:
                return new ReadResult<IReadOnlyList<PageRevision>>.NotFound();
            case ReadResult<Page>.Denied denied:
                return new ReadResult<IReadOnlyList<PageRevision>>.Denied(denied.Reason);
        }

        var revisions = await _db.PageRevisions
            .Where(r => r.PageId == pageId)
            .OrderByDescending(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);
        return new ReadResult<IReadOnlyList<PageRevision>>.Found(revisions);
    }

    public async Task<ReadResult<IReadOnlyList<PageTreeEntry>>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken);
        if (space is null)
        {
            return new ReadResult<IReadOnlyList<PageTreeEntry>>.NotFound();
        }

        var spaceGrants = await _permissions.LoadSpaceGrantsAsync(spaceId, cancellationToken);

        // No space access at all means no view of anything in it - to the caller an
        // empty tree, not an error, matching "invisible, not merely unopenable"
        // (design.md §6.7); internally a Denied so §7 can record the refused browse.
        // Access grants only: a role grant confers no visibility (§6.4), so a Space-admin
        // with no access grant is refused here exactly like a stranger. This is the only
        // Denied this method produces: a node the caller fails during the walk below is
        // not a denied request - the browse succeeded and shows that node as a protected
        // placeholder (see the interface doc) - and is neither reported nor audited here.
        var access = EffectivePermissionCalculator.ComputeSpaceAccess(spaceGrants, principal);
        if (access is null)
        {
            return new ReadResult<IReadOnlyList<PageTreeEntry>>.Denied(EffectivePermissionCalculator.NoSpaceAccessReason);
        }

        // Two queries total regardless of tree depth or size: every live page in the
        // space, and every restriction attached to any of them. The walk below is
        // then pure in-memory recursion - exactly what AncestorPath exists to make cheap.
        // Both actions are loaded (not just View, which gating alone would need):
        // the same rows also feed each node's HasRestrictions marker and
        // OwnViewRestrictions list (see PageTreeNode's doc for the leak posture),
        // still without a per-node query.
        var pages = await _db.Pages.Where(p => p.SpaceId == spaceId).ToListAsync(cancellationToken);
        var pageIds = pages.Select(p => p.Id).ToArray();
        var restrictions = pageIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && pageIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        // design.md §21: the tree is the one read path that does NOT go through
        // PermissionContextLoader - it walks the whole space in memory precisely to avoid
        // per-node work, so it assembles the calculator's inputs itself. That made it the
        // place a new view gate was easiest to forget, so the marking is loaded here in
        // the same shape and on the same constant-query budget: ONE more query for every
        // marking in the space, countries and selectors included, never one per node - and
        // the gates themselves are the calculator's own (BuildEntry), never a hand-rolled
        // subset.
        var markingsByPageId = pageIds.Length == 0
            ? new Dictionary<Guid, ProtectiveMarking>()
            : (await _db.PageMarkings
                    .Include(m => m.Countries)
                    .Include(m => m.Selectors)
                    .Where(m => pageIds.Contains(m.PageId))
                    .ToListAsync(cancellationToken))
                .ToDictionary(m => m.PageId, m => m.ToMarking());

        // Own rules in the loader's contractual order (CreatedAtUtc then Id), so the
        // first failing restriction a node reports is the same one every other read path
        // would name for the same page.
        var restrictionsByPageId = restrictions
            .GroupBy(r => r.PageId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AccessRule>)g.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).ToList());
        // SortOrder then Id, matching GetVisibleChildIdsAsync: siblings sharing a
        // SortOrder (every bulk import produces plenty) must not reshuffle between
        // requests, and the tree and the `children` field must agree about sibling order
        // or the same subtree renders differently depending on how it was reached.
        var childrenByParentId = pages
            .Where(p => p.ParentPageId is not null)
            .GroupBy(p => p.ParentPageId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToList());
        var rootPages = pages.Where(p => p.ParentPageId is null).OrderBy(p => p.SortOrder).ThenBy(p => p.Id);

        var result = new List<PageTreeEntry>();
        foreach (var root in rootPages)
        {
            result.Add(BuildEntry(root, childrenByParentId, restrictionsByPageId, markingsByPageId, access, principal, _db.SelectorCatalog));
        }

        return new ReadResult<IReadOnlyList<PageTreeEntry>>.Found(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Page>> GetPagesAsync(
        IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default)
    {
        var ids = pageIds.Distinct().ToArray();
        var result = new Dictionary<Guid, Page>();
        if (ids.Length == 0)
        {
            return result;
        }

        // Four queries for the whole batch regardless of its size: the pages, then the
        // loader's three (grants, restriction chains, markings). Same shape as
        // GetVisibleChildIdsAsync, and the reason is the same — the per-page alternative
        // is N*3 round trips that cannot be parallelized on one DbContext.
        var pages = await _db.Pages.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        if (pages.Count == 0)
        {
            return result;
        }

        var batch = await _permissions.LoadBatchAsync(
            pages.Select(PermissionSubject.For).ToList(), cancellationToken);

        foreach (var page in pages)
        {
            // Replica status is irrelevant to canView (design.md §6.4), so false here
            // exactly as on the single-page path.
            if (batch.For(PermissionSubject.For(page), isReplicaSpace: false).Compute(principal).CanView)
            {
                result[page.Id] = page;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetVisibleChildIdsAsync(
        IReadOnlyCollection<Guid> parentPageIds, Principal principal, CancellationToken cancellationToken = default)
    {
        var parentIds = parentPageIds.Distinct().ToArray();
        if (parentIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        // Four queries for the whole batch regardless of how many parents are in it: the
        // children, then the loader's three (grants, restriction chains, markings). The
        // per-child work afterwards is pure in-memory rule evaluation.
        var children = await _db.Pages
            .Where(p => p.ParentPageId != null && parentIds.Contains(p.ParentPageId.Value))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);

        var result = parentIds.ToDictionary(id => id, _ => (IReadOnlyList<Guid>)Array.Empty<Guid>());
        if (children.Count == 0)
        {
            return result;
        }

        var batch = await _permissions.LoadBatchAsync(
            children.Select(PermissionSubject.For).ToList(), cancellationToken);

        var visibleByParent = new Dictionary<Guid, List<Guid>>();
        foreach (var child in children)
        {
            // Each child on its own terms - its own chain, its own marking. Replica status
            // is irrelevant to canView (design.md §6.4), so false regardless of origin.
            if (!batch.For(PermissionSubject.For(child), isReplicaSpace: false).Compute(principal).CanView)
            {
                continue;
            }

            if (!visibleByParent.TryGetValue(child.ParentPageId!.Value, out var siblings))
            {
                siblings = [];
                visibleByParent[child.ParentPageId.Value] = siblings;
            }

            siblings.Add(child.Id);
        }

        foreach (var (parentId, visible) in visibleByParent)
        {
            result[parentId] = visible;
        }

        return result;
    }

    /// <summary>
    /// One node's entry: a <see cref="PageTreeNode"/> with its children when the page's
    /// own view gates pass, otherwise a <see cref="ProtectedTreeNode"/> leaf carrying every
    /// failed gate - and its subtree is never walked. Never re-checks ancestor
    /// restrictions: a child is only ever visited after its parent already passed, so
    /// "carried down the recursion" (design.md §6.7) means each node costs one
    /// restriction-list lookup, not a walk back up the tree. The gates themselves are the
    /// calculator's own view ladder after S (<see cref="EffectivePermissionCalculator.EvaluateViewGates"/>
    /// - C, E, G, N, then the node's own view restrictions), in the non-short-circuiting
    /// form so a denied node's reasons are complete (§6.7's disclosed denial), and a
    /// parity test pins each node's verdict to <c>GetPageAsync</c>'s.
    ///
    /// <para>The protective marking (design.md §21) is checked per node, NOT carried down
    /// - a marking is a page's own value and a child may legitimately sit below its
    /// parent's level. The consequence is that failing the marking for a page still hides
    /// its subtree, even a child the caller could read: a tree cannot render a node whose
    /// parent is a placeholder, and the more-hidden direction is the safe one. Such a
    /// child stays reachable by id and through search, both of which check it on its
    /// own.</para>
    /// </summary>
    private static PageTreeEntry BuildEntry(
        Page page,
        IReadOnlyDictionary<Guid, List<Page>> childrenByParentId,
        IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> restrictionsByPageId,
        IReadOnlyDictionary<Guid, ProtectiveMarking> markingsByPageId,
        SpaceAccess access,
        Principal principal,
        SelectorCatalog catalog)
    {
        // design.md §15: this walk decides each node itself rather than through Compute,
        // so it has to record that calculator's TELEMETRY too or the rule-engine metrics
        // silently exclude the busiest read path in the product. They did once:
        // `rocketwiki.access.rule_evaluations` and `rocketwiki.access.permission_checks`
        // counted every page fetch, search post-filter and label listing but not one node
        // of a tree browse - so a "denials by category" dashboard showed none of the
        // classification pruning happening here, which is exactly the signal an operator
        // would look for. Duration is measured over the same span the calculator
        // measures: one node's decision, not the whole walk.
        var startTimestamp = Stopwatch.GetTimestamp();

        // Fail closed on a page with no marking row, the same substitution
        // PermissionContextBatch.MarkingFor makes (design.md §21).
        var marking = markingsByPageId.GetValueOrDefault(page.Id) ?? ProtectiveMarking.FailClosed;
        var ownRestrictions = restrictionsByPageId.GetValueOrDefault(page.Id) ?? [];

        // Edit restrictions in the list mark the node as restricted but never gate
        // visibility and never expose their contents here (PageTreeNode doc); the view
        // ladder ignores them by construction.
        var gates = EffectivePermissionCalculator.EvaluateViewGates(
            access, marking, ownRestrictions, catalog, principal, shortCircuit: false);
        var failed = gates.Where(g => !g.Passed).ToList();
        if (failed.Count > 0)
        {
            RecordNodeCheck(false, failed[0].Reason, startTimestamp);
            // S passed for the whole space (or this walk would not be running), so the
            // marking travels with the denial (§21.8) and every failed gate is listed.
            return new ProtectedTreeNode(page.SortOrder, PageDenial.AfterSpaceAccess(failed, marking));
        }

        // Only reached for rules the caller passed - an unpassed view rule made the node
        // a placeholder above, so OwnViewRestrictions can never carry an expression the
        // caller doesn't already satisfy.
        var ownViewRestrictions = gates
            .Where(g => g.Kind == GateKind.ViewRestriction)
            .Select(g => new PageTreeRestriction(g.RuleId!.Value, g.ExpressionJson!))
            .ToList();
        var hasRestrictions = ownRestrictions.Count > 0;

        // The node survived: recorded before recursing, so the duration is this node's
        // own decision rather than its whole subtree's.
        RecordNodeCheck(true, null, startTimestamp);

        var children = new List<PageTreeEntry>();
        if (childrenByParentId.TryGetValue(page.Id, out var childPages))
        {
            foreach (var child in childPages)
            {
                children.Add(BuildEntry(child, childrenByParentId, restrictionsByPageId, markingsByPageId, access, principal, catalog));
            }
        }

        // The marking carried out is the one resolved at the top of this method - the
        // very value the gates just passed on. Not re-loaded downstream: see
        // PageTreeNode's doc for why a tree that displayed a different marking from the
        // one it enforced would be the wrong kind of wrong.
        return new PageTreeNode(
            page.Id, page.Title, page.Icon, page.Slug, page.SortOrder, hasRestrictions, ownViewRestrictions,
            PageMarkingView.From(marking, catalog), children);
    }

    /// <summary>
    /// One tree node's view decision, in the shape
    /// <see cref="EffectivePermissionCalculator.Compute"/> would have recorded it. canEdit
    /// is reported false throughout: the walk never computes it (a placeholder is a view
    /// question), and claiming otherwise would put a fabricated edit verdict into a
    /// metric operators read as fact. The denial reason is passed through
    /// <c>CategorizeDenialReason</c> inside RecordPermissionCheck, so only the bounded
    /// category reaches the tag - a rule id never becomes a metric dimension (§15).
    /// </summary>
    private static void RecordNodeCheck(bool canView, string? denialReason, long startTimestamp) =>
        CoreTelemetry.RecordPermissionCheck(
            new EffectivePermission(canView, false, canView ? null : denialReason, denialReason),
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);

    /// <summary>
    /// Replica status is irrelevant to canView (design.md §6.4: it only ever affects
    /// canEdit), so this is always false here regardless of the space's origin.
    /// The permission-check counter (rocketwiki.access.permission_checks) and its
    /// bounded denial-category tag are emitted inside Compute itself (design.md §15)
    /// - unchanged by the richer ReadResult return, which carries the *specific*
    /// reason to the audit row only.
    /// </summary>
    private async Task<EffectivePermission> ComputePermissionAsync(Page page, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(page, isReplicaSpace: false, cancellationToken);
        return context.Compute(principal);
    }
}
