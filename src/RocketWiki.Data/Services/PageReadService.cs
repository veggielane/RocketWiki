using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
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

    public async Task<ReadResult<Page>> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return new ReadResult<Page>.NotFound();
        }

        // Fail closed on an unresolvable space even though the permission computation
        // below no longer needs the row: no space, no grants to hold a role under.
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new ReadResult<Page>.NotFound();
        }

        var permission = await ComputePermissionAsync(page, principal, cancellationToken);
        return permission.CanView
            ? new ReadResult<Page>.Found(page)
            : new ReadResult<Page>.Denied(permission.ViewDenialReason ?? "no-space-role");
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

    public async Task<ReadResult<IReadOnlyList<PageTreeNode>>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken);
        if (space is null)
        {
            return new ReadResult<IReadOnlyList<PageTreeNode>>.NotFound();
        }

        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == spaceId)
            .ToListAsync(cancellationToken);

        // No space role at all means no view of anything in it - to the caller an empty
        // tree, not an error, matching "invisible, not merely unopenable" (design.md
        // §6.7); internally a Denied so §7 can record the refused browse. This is the
        // only Denied this method produces: a node pruned during the walk below is not
        // a denied request - the browse succeeded and simply shows less (see the
        // interface doc) - so pruning stays unreported on purpose.
        if (EffectivePermissionCalculator.ComputeSpaceRole(spaceGrants, principal) is null)
        {
            return new ReadResult<IReadOnlyList<PageTreeNode>>.Denied("no-space-role");
        }

        // Two queries total regardless of tree depth or size: every live page in the
        // space, and every restriction attached to any of them. The walk below is
        // then pure in-memory recursion - exactly what AncestorPath exists to make cheap.
        // Both actions are loaded (not just View, which pruning alone would need):
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
        // per-node work, so it hand-rolls the restriction evaluation the loader would
        // otherwise order for it. That made it the place a new view gate is easiest to
        // forget, so the marking is loaded here in the same shape and on the same
        // constant-query budget: ONE more query for every marking in the space, countries
        // included, never one per node.
        var markingsByPageId = pageIds.Length == 0
            ? new Dictionary<Guid, ProtectiveMarking>()
            : (await _db.PageMarkings
                    .Include(m => m.Countries)
                    .Where(m => pageIds.Contains(m.PageId))
                    .ToListAsync(cancellationToken))
                .ToDictionary(m => m.PageId, m => m.ToMarking());

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
            var node = BuildNodeIfVisible(root, childrenByParentId, restrictionsByPageId, markingsByPageId, principal);
            if (node is not null)
            {
                result.Add(node);
            }
        }

        return new ReadResult<IReadOnlyList<PageTreeNode>>.Found(result);
    }

    /// <summary>
    /// Returns null - pruning this page and its entire subtree - the moment this page's
    /// own view restrictions fail. Never re-checks ancestor restrictions: a child is
    /// only ever visited after its parent already passed, so "carried down the
    /// recursion" (design.md §6.7) means each node costs one restriction-list lookup,
    /// not a walk back up the tree.
    ///
    /// <para>The protective marking (design.md §21) is checked per node, NOT carried down
    /// - a marking is a page's own value and a child may legitimately sit below its
    /// parent's level. The consequence is that failing clearance for a page still prunes
    /// its subtree, even a child the caller is cleared for: a tree cannot render a node
    /// whose parent is absent, and the more-hidden direction is the safe one. Such a
    /// child stays reachable by id and through search, both of which check it on its
    /// own.</para>
    /// </summary>
    private static PageTreeNode? BuildNodeIfVisible(
        Page page,
        IReadOnlyDictionary<Guid, List<Page>> childrenByParentId,
        IReadOnlyDictionary<Guid, List<AccessRule>> restrictionsByPageId,
        IReadOnlyDictionary<Guid, ProtectiveMarking> markingsByPageId,
        Principal principal)
    {
        // Fail closed on a page with no marking row, the same substitution
        // PermissionContextBatch.MarkingFor makes (design.md §21).
        var marking = markingsByPageId.GetValueOrDefault(page.Id) ?? ProtectiveMarking.FailClosed;
        if (!ClearanceGate.Check(marking, principal).IsAllowed)
        {
            return null;
        }

        var ownViewRestrictions = new List<PageTreeRestriction>();
        var hasRestrictions = false;
        if (restrictionsByPageId.TryGetValue(page.Id, out var ownRestrictions))
        {
            hasRestrictions = ownRestrictions.Count > 0;
            foreach (var rule in ownRestrictions)
            {
                if (rule.Action != PageAction.View)
                {
                    // Edit restrictions mark the node as restricted but never gate
                    // visibility and never expose their contents here (PageTreeNode doc).
                    continue;
                }

                if (!AccessRuleExpression.Evaluate(rule.ExpressionJson, principal).IsMatch)
                {
                    return null;
                }

                // Only reached for rules the caller passed - an unpassed view rule
                // pruned the node above, so OwnViewRestrictions can never carry an
                // expression the caller doesn't already satisfy.
                ownViewRestrictions.Add(new PageTreeRestriction(rule.Id, rule.ExpressionJson));
            }
        }

        var children = new List<PageTreeNode>();
        if (childrenByParentId.TryGetValue(page.Id, out var childPages))
        {
            foreach (var child in childPages)
            {
                var childNode = BuildNodeIfVisible(child, childrenByParentId, restrictionsByPageId, markingsByPageId, principal);
                if (childNode is not null)
                {
                    children.Add(childNode);
                }
            }
        }

        // The marking carried out is the one resolved at the top of this method - the
        // very value the clearance gate just passed on. Not re-loaded downstream: see
        // PageTreeNode's doc for why a tree that displayed a different marking from the
        // one it enforced would be the wrong kind of wrong.
        return new PageTreeNode(
            page.Id, page.Title, page.Slug, page.SortOrder, hasRestrictions, ownViewRestrictions,
            PageMarkingView.From(marking), children);
    }

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
