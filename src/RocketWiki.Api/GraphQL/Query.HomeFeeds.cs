using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// One page change in the activity feed.
///
/// <para><b>Everything about the page comes from <see cref="Page"/>, not from fields on
/// this row.</b> That is the rule <c>SearchHitType</c> and <c>PageQueryRow</c> both apply
/// and state: a projected title or space key is an unauthorized copy that routes around
/// the object-level Page resolvers every other path serves page data through (§6.7/§8).
/// It also means the page's marking (§21) arrives through <c>page { marking }</c> — the
/// same value, from the same gated resolver, rather than a second rendering of a
/// classification.</para>
///
/// <para>What IS on the row is what belongs to the change rather than the page: who made
/// it, which revision, whether it created the page, and when.</para>
/// </summary>
public sealed record ActivityFeedItem(
    Guid PageId,
    Guid AuthorUserId,
    int RevisionNumber,
    bool IsCreate,
    DateTime OccurredAtUtc);

/// <summary>
/// A page the caller has written on, surfaced because it has not been touched in a while.
/// Page data comes through <c>page { … }</c> — see <see cref="ActivityFeedItem"/>.
///
/// <para><b>Carries no timestamp of its own.</b> The ordering fact is "when was this page
/// last touched by anyone", which is <c>Page.UpdatedAtUtc</c> — a page field, already
/// reachable through <c>page { updatedAtUtc }</c>. Copying it onto the row would be the
/// projected copy this whole shape exists to avoid, and would let the row's value drift
/// from the page's. So the row is the page id and nothing else, like
/// <c>PageQueryRow</c>.</para>
/// </summary>
public sealed record StaleContentItem(Guid PageId);

/// <summary>
/// A page the caller has viewed. Page data comes through <c>page { … }</c> — see
/// <see cref="ActivityFeedItem"/>.
/// </summary>
public sealed record RecentlyViewedItem(Guid PageId, DateTime LastViewedAtUtc);

public partial class Query
{
    /// <summary>
    /// How many candidate rows each feed reads before permission filtering. Feeds are
    /// cross-space, so the candidate set is "every recent revision anywhere" and most of
    /// it may belong to spaces the caller cannot see — the window is what keeps that
    /// bounded.
    ///
    /// <para><b>The honest consequence:</b> paging deeper than this window returns
    /// nothing, even if more visible items exist further back. The alternative is
    /// permission-filtering an unbounded history to answer one homepage panel. A feed is
    /// a glance at what is recent, not an archive — and the archive already exists as
    /// search, which pages properly.</para>
    /// </summary>
    private const int FeedCandidateWindow = 500;

    /// <summary>
    /// The largest page the feeds advertise. <b>Deliberately far below the 100 the other
    /// paged roots allow, and the reason is the cost analyser, not the resolver.</b>
    ///
    /// <para>Hot Chocolate's default field-cost budget is 1000. <c>[UsePaging]</c> emits
    /// <c>@listSize(slicingArguments: ["first"])</c>, so a connection's cost is
    /// <c>first × (cost of one row's selection)</c>. A feed row is not scalars: it resolves
    /// <c>page</c>, the page's <c>marking</c>, and (on activity) <c>author</c>, each a
    /// resolver-backed field weighted 10. Measured against the real schema, the homepage's
    /// selection costs 40 per row — so 24 rows is the true ceiling and <c>first: 25</c> is
    /// rejected at validation with HC0047, before any resolver runs.</para>
    ///
    /// <para>So an advertised 100 would be a page size no caller could ever request — the
    /// exact defect just fixed on <c>auditEvents</c>, which is why this number is measured
    /// rather than chosen. 20 leaves only about one row-field of headroom: <b>adding another
    /// object-valued field to a feed selection will breach the budget</b>, and the fix then
    /// is a deliberate budget, not a quieter page size.</para>
    ///
    /// <para>Note this binds here and not on <c>search</c> or <c>pageQuery</c>, whose
    /// hand-written connection types carry no <c>@listSize</c> and so are never multiplied
    /// by their page size. That is an inconsistency in the guard, not a licence to copy the
    /// exemption — these feeds keep the honest declaration.</para>
    /// </summary>
    private const int FeedMaxPageSize = 20;

    /// <summary>
    /// design.md §8: recent page creates and edits across every space the caller can view,
    /// newest first.
    ///
    /// <para><b>Permission filtering is the whole feature.</b> A cross-space feed reads
    /// candidates from every space at once, so canView is not a refinement here — it is
    /// the only thing standing between "recent activity" and "a list of every page title
    /// in the instance". Filtering runs through <see cref="IPagePermissionReadService"/>,
    /// the same batched canView the resolvers use (four queries per batch regardless of
    /// size), and an item that fails is <b>absent</b> — never replaced by a count or a
    /// placeholder (§6.7).</para>
    ///
    /// <para>Revisions rather than the audit table, per §7's division of labour: the audit
    /// log is the regulated record of who did what, and reading it to render a social feed
    /// would make an access-controlled compliance artefact into a product surface. A
    /// revision is the content fact this feed is actually about.</para>
    ///
    /// <para><b><c>totalCount</c> is exact, and deliberately not saturated the way
    /// <c>search</c>'s is.</b> Search caps its count because the count itself is computed
    /// over an estate the caller cannot see, so a large number is a signal about restricted
    /// content (§9.1/§21.8). This count is taken <b>after</b> canView filtering, over items
    /// the caller can already read in full — it counts what is shown, never what was
    /// withheld, which is the distinction §6.7 actually draws. It is free here besides: the
    /// resolver returns a materialised, already-filtered list.</para>
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    [UsePaging(IncludeTotalCount = true, MaxPageSize = FeedMaxPageSize)]
    public async Task<IEnumerable<ActivityFeedItem>> ActivityFeed(
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IPagePermissionReadService permissions,
        CancellationToken cancellationToken)
    {
        var principal = RequireFeedPrincipal(claimsPrincipal, principalAccessor);
        if (principal is null)
        {
            return [];
        }

        // Candidates first, authorization second — but nothing about a candidate reaches
        // the caller until it has passed. The projection deliberately carries no title or
        // content: this list may contain pages the caller cannot see, and the less of them
        // it holds, the less there is to leak by accident.
        var candidates = await db.PageRevisions.AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.RevisionNumber)
            .Take(FeedCandidateWindow)
            .Select(r => new { r.PageId, r.AuthorUserId, r.RevisionNumber, r.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var viewable = await ViewablePageIdsAsync(candidates.Select(c => c.PageId), principal, permissions, cancellationToken);

        return candidates
            .Where(c => viewable.Contains(c.PageId))
            .Select(c => new ActivityFeedItem(
                c.PageId,
                c.AuthorUserId,
                c.RevisionNumber,
                // Revision 1 is the create; there is no separate "created" fact on Page,
                // which is also why the brief's Page.CreatedByUserId does not exist.
                IsCreate: c.RevisionNumber == 1,
                c.CreatedAtUtc))
            .ToList();
    }

    /// <summary>
    /// design.md §8: pages the caller has written on, <b>oldest first</b> — the stalest
    /// surface at the top, which is the point of the feed rather than an ordering
    /// preference.
    ///
    /// <para>"Written on" is: authored any revision, or credited as a contributor on one.
    /// The brief also named <c>Page.CreatedByUserId</c>, which does not exist — a page's
    /// creator is the author of its revision 1, so that clause is already covered by the
    /// first, exactly and without a schema change.</para>
    ///
    /// <para><b>Membership is "written on by me"; the ORDER is "last touched by anyone".</b>
    /// Those are deliberately different questions. A page you wrote that someone else has
    /// since maintained is not neglected, so ordering by how long since <i>you</i> touched
    /// it would surface well-tended pages and bury genuinely abandoned ones. The ordering
    /// key is therefore <c>Page.UpdatedAtUtc</c> ascending — which is also why the row
    /// carries no timestamp of its own: the number the sort is on is
    /// <c>page { updatedAtUtc }</c>, so the frontend shows the same value it sorted by.</para>
    ///
    /// <para>canView is re-checked at read time, so a page you edited and later lost
    /// access to drops out silently (§6.7). Authorship is not a standing claim on
    /// content.</para>
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    [UsePaging(IncludeTotalCount = true, MaxPageSize = FeedMaxPageSize)]
    public async Task<IEnumerable<StaleContentItem>> MyStaleContent(
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] IPagePermissionReadService permissions,
        CancellationToken cancellationToken)
    {
        var principal = RequireFeedPrincipal(claimsPrincipal, principalAccessor);
        var actingUserId = actingUserAccessor.ActingUserId;
        if (principal is null || actingUserId is null)
        {
            return [];
        }

        var candidates = await db.Pages.AsNoTracking()
            .Where(p =>
                db.PageRevisions.Any(r => r.PageId == p.Id && r.AuthorUserId == actingUserId)
                || db.PageRevisionContributors.Any(c =>
                    c.UserId == actingUserId && c.PageRevision!.PageId == p.Id))
            .OrderBy(p => p.UpdatedAtUtc)
            .ThenBy(p => p.Id)
            .Take(FeedCandidateWindow)
            .Select(p => new { p.Id, p.UpdatedAtUtc })
            .ToListAsync(cancellationToken);

        var viewable = await ViewablePageIdsAsync(candidates.Select(c => c.Id), principal, permissions, cancellationToken);

        return candidates
            .Where(c => viewable.Contains(c.Id))
            .Select(c => new StaleContentItem(c.Id))
            .ToList();
    }

    /// <summary>
    /// design.md §8: the caller's own recent page views, most recent first, one entry per
    /// page.
    ///
    /// <para><b>Read from the existing <c>page.view</c> audit rows</b> rather than a new
    /// tracking table — the same rows analytics reads. Worth being explicit about why that
    /// is acceptable here when §7 keeps the audit log out of product surfaces: this reads
    /// only the caller's OWN rows, so it discloses nothing the caller did not do, and it
    /// adds no new record of anybody's reading. A feed of someone else's views would be a
    /// different feature and a much worse idea.</para>
    ///
    /// <para><b>canView is re-checked now, not trusted from then.</b> The view happened
    /// under whatever permissions applied at the time; a page since restricted, or since
    /// deleted, must not reappear because the caller once opened it (§6.7). That
    /// re-check is why this cannot be served from the audit rows alone.</para>
    ///
    /// <para>The scan is bounded by the caller's own audit history via the existing
    /// <c>(UserId, TimestampUtc)</c> index, so no migration was needed. The window
    /// over-fetches because one page viewed repeatedly consumes many rows for a single
    /// feed entry.</para>
    ///
    /// <para><b>No <c>totalCount</c> here</b>, unlike the other two. It would be safe — this
    /// is the caller's own history — but it would not be meaningful: the number counts
    /// distinct pages inside an arbitrary window of view rows, so it measures the window
    /// rather than anything a reader could act on. A count nobody can interpret is worse
    /// than no count.</para>
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    [UsePaging(IncludeTotalCount = false, MaxPageSize = FeedMaxPageSize)]
    public async Task<IEnumerable<RecentlyViewedItem>> MyRecentlyViewed(
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] IPagePermissionReadService permissions,
        CancellationToken cancellationToken)
    {
        var principal = RequireFeedPrincipal(claimsPrincipal, principalAccessor);
        var actingUserId = actingUserAccessor.ActingUserId;
        if (principal is null || actingUserId is null)
        {
            return [];
        }

        var views = await db.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == actingUserId
                && e.Action == "page.view"
                && e.SubjectId != null)
            .OrderByDescending(e => e.TimestampUtc)
            .Take(FeedCandidateWindow)
            .Select(e => new { PageId = e.SubjectId!.Value, e.TimestampUtc })
            .ToListAsync(cancellationToken);

        // Distinct by page, keeping the most recent view. Done after the ordered read so
        // "latest" is exactly the first row seen for each page.
        var latestByPage = new Dictionary<Guid, DateTime>();
        foreach (var view in views)
        {
            latestByPage.TryAdd(view.PageId, view.TimestampUtc);
        }

        var viewable = await ViewablePageIdsAsync(latestByPage.Keys, principal, permissions, cancellationToken);

        return latestByPage
            .Where(entry => viewable.Contains(entry.Key))
            .OrderByDescending(entry => entry.Value)
            .Select(entry => new RecentlyViewedItem(entry.Key, entry.Value))
            .ToList();
    }

    /// <summary>
    /// The one canView gate all three feeds share, so there is a single place where a feed
    /// decides what the caller may see. Runs through
    /// <see cref="IPagePermissionReadService"/> — the batched path the resolvers use — so
    /// a feed cannot acquire its own weaker opinion about access.
    ///
    /// <para>Fail-closed twice over: a page id absent from the result (deleted, or gone
    /// between the candidate read and this call) is not viewable, and an entry present
    /// with CanView false is not viewable. The service's own contract requires callers to
    /// treat a missing entry as deny, and this is that treatment.</para>
    /// </summary>
    private static async Task<HashSet<Guid>> ViewablePageIdsAsync(
        IEnumerable<Guid> pageIds,
        Principal principal,
        IPagePermissionReadService permissions,
        CancellationToken cancellationToken)
    {
        var distinct = pageIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var facts = await permissions.GetPermissionFactsAsync(distinct, principal, cancellationToken);

        return facts
            .Where(entry => entry.Value.Permission.CanView)
            .Select(entry => entry.Key)
            .ToHashSet();
    }

    /// <summary>
    /// Anonymous callers get the empty shape every read root gives (§6.7), not an error —
    /// a homepage that throws for a signed-out visitor is worse than one that shows
    /// nothing, and there is no existence to protect either way.
    /// </summary>
    private static Principal? RequireFeedPrincipal(
        ClaimsPrincipal claimsPrincipal, ICurrentPrincipalAccessor principalAccessor) =>
        claimsPrincipal.Identity?.IsAuthenticated == true ? principalAccessor.Current : null;
}
