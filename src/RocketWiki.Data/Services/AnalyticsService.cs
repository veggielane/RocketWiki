using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IAnalyticsService"/>. Lives in Data for the same reason
/// PageService does: it needs the DbContext, and Core may not reference EF.
///
/// <para>The whole design turns on one decision — the visible page set is obtained
/// from <see cref="IPageReadService.GetPageTreeAsync"/> rather than from a query
/// written here. That walk already applies space grants, accumulated page
/// restrictions and the §21 clearance gate, and it is the same walk the tree and
/// search use. Re-deriving visibility in an aggregation query would be a second
/// implementation of the rule engine that nothing keeps in step, and the failure
/// mode would be silent: a number slightly too large, in a screen whose whole
/// purpose is numbers nobody checks by hand.</para>
/// </summary>
public sealed class AnalyticsService(RocketWikiDbContext db, IPageReadService pageReads) : IAnalyticsService
{
    /// <summary>How many rows a ranked panel returns. Small on purpose: these are
    /// "what should I look at" lists, and a long tail of one-view pages is noise that
    /// also widens what a single screen discloses about reading habits.</summary>
    private const int TopN = 10;

    /// <summary>A page nothing has touched in this long counts as stale. Not
    /// configurable yet — one honest default beats a setting nobody tunes.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(180);

    public async Task<ReadResult<AnalyticsReport>> GetReportAsync(
        string? spaceKey,
        DateTime fromUtc,
        DateTime toUtc,
        Principal principal,
        bool isInstanceAdmin,
        CancellationToken cancellationToken = default)
    {
        var spaces = await ResolveAdministeredSpacesAsync(spaceKey, principal, isInstanceAdmin, cancellationToken);
        if (spaces is null)
        {
            // One answer for "no such space" and "not yours to administer" (§6.7): the
            // caller learns nothing about keys they do not administer, and the GraphQL
            // layer collapses both to null anyway.
            return new ReadResult<AnalyticsReport>.Denied("not-an-administrator");
        }

        // The visible set, inherited from the real gate rather than re-derived. Also
        // the only place a page's title comes from, so a page that has since been
        // trashed or become invisible drops out of every panel at once.
        var visiblePages = new Dictionary<Guid, PageIdentity>();
        foreach (var space in spaces)
        {
            if (await pageReads.GetPageTreeAsync(space.Id, principal, cancellationToken)
                is ReadResult<IReadOnlyList<PageTreeNode>>.Found tree)
            {
                Flatten(tree.Value, space.Key, visiblePages);
            }
        }

        var scope = new AnalyticsScope(spaceKey, fromUtc, toUtc, visiblePages.Count);
        if (visiblePages.Count == 0)
        {
            return new ReadResult<AnalyticsReport>.Found(EmptyReport(scope));
        }

        var pageIds = visiblePages.Keys.ToList();
        var spaceKeys = spaces.Select(s => s.Key).ToList();

        // design.md §7/§15: "site-wide" is an INSTANCE-ADMIN scope, not "whatever a null
        // spaceKey means". A space admin may ask for a null spaceKey - that is how they
        // get a report across every space they administer (see ResolveAdministeredSpacesAsync)
        // - but that must not silently promote them to the site-wide search panel below.
        // The page/people panels are already safe by construction: they are computed from
        // visiblePages, which came from the real canView gate. The search panels are not,
        // because they read `search.query` audit rows directly, and those rows carry raw
        // query text for EVERY user in EVERY space. §7 reserves the audit log to instance
        // admins, and §15 names an unregulated who-searched-what record as exactly the
        // thing not to build by accident.
        var siteWide = isInstanceAdmin && spaceKey is null;

        // Page-subject events, restricted to the visible set. Materialized once and
        // sliced in memory: the alternative is six round trips over the same window
        // that must all agree about which pages count.
        var pageEvents = await db.AuditEvents.AsNoTracking()
            .Where(e => e.TimestampUtc >= fromUtc && e.TimestampUtc < toUtc
                && e.Outcome == AuditOutcome.Success
                && e.SubjectId != null && pageIds.Contains(e.SubjectId.Value))
            .Select(e => new EventRow(e.Action, e.SubjectId!.Value, e.UserId, e.TimestampUtc))
            .ToListAsync(cancellationToken);

        var views = pageEvents.Where(e => e.Action == "page.view").ToList();
        var edits = pageEvents.Where(e => e.Action is "page.edit" or "page.create").ToList();

        var report = new AnalyticsReport(
            scope,
            BuildActivitySeries(views, edits, fromUtc, toUtc),
            RankPages(views, visiblePages),
            RankPages(edits, visiblePages),
            await RankPeopleAsync(views, cancellationToken),
            await RankPeopleAsync(edits, cancellationToken),
            await BuildHealthAsync(visiblePages, views, cancellationToken),
            await BuildSearchesAsync(spaceKeys, siteWide, fromUtc, toUtc, zeroResultsOnly: false, cancellationToken),
            await BuildSearchesAsync(spaceKeys, siteWide, fromUtc, toUtc, zeroResultsOnly: true, cancellationToken));

        return new ReadResult<AnalyticsReport>.Found(report);
    }

    /// <summary>
    /// The spaces this caller may report on: all viewable ones for an instance admin,
    /// only those they hold SpaceAdmin in otherwise. Null means "report on nothing",
    /// which the caller turns into a refusal.
    /// </summary>
    private async Task<List<Space>?> ResolveAdministeredSpacesAsync(
        string? spaceKey, Principal principal, bool isInstanceAdmin, CancellationToken cancellationToken)
    {
        // Canonicalized like every other space-key filter (SpaceKeys.Canonical), so a
        // report requested for "eng" is the ENG report rather than the
        // not-an-administrator refusal a non-matching key produces.
        var canonicalKey = SpaceKeys.CanonicalOrNull(spaceKey);

        // No archived filter of its own: the DbContext's global query filter already
        // excludes them, and restating it here would be a second rule to keep in step.
        var candidates = await db.Spaces
            .Where(s => canonicalKey == null || s.Key == canonicalKey)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        if (isInstanceAdmin)
        {
            return candidates;
        }

        // Space-admin scope. Evaluated through the same rule expressions the mutation
        // gates use, so "administers this space" means one thing in the product.
        var adminGrants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.Role == SpaceRole.SpaceAdmin)
            .ToListAsync(cancellationToken);

        var administered = candidates
            .Where(space => adminGrants.Any(g => g.SpaceId == space.Id
                && AccessRuleExpression.Evaluate(g.ExpressionJson, principal).IsMatch))
            .ToList();

        return administered.Count == 0 ? null : administered;
    }

    private static void Flatten(IReadOnlyList<PageTreeNode> nodes, string spaceKey, Dictionary<Guid, PageIdentity> into)
    {
        foreach (var node in nodes)
        {
            into[node.Id] = new PageIdentity(node.Title, node.Slug, spaceKey);
            Flatten(node.Children, spaceKey, into);
        }
    }

    /// <summary>
    /// One point per day across the whole window, including days nothing happened.
    /// A sparse series drawn as a line implies activity between the points it does
    /// have, which is precisely backwards for a quiet week.
    /// </summary>
    private static List<ActivityPoint> BuildActivitySeries(
        List<EventRow> views, List<EventRow> edits, DateTime fromUtc, DateTime toUtc)
    {
        var viewsByDay = views.GroupBy(e => DateOnly.FromDateTime(e.TimestampUtc)).ToDictionary(g => g.Key, g => g.Count());
        var editsByDay = edits.GroupBy(e => DateOnly.FromDateTime(e.TimestampUtc)).ToDictionary(g => g.Key, g => g.Count());

        var points = new List<ActivityPoint>();
        for (var day = DateOnly.FromDateTime(fromUtc); day < DateOnly.FromDateTime(toUtc); day = day.AddDays(1))
        {
            points.Add(new ActivityPoint(day, viewsByDay.GetValueOrDefault(day), editsByDay.GetValueOrDefault(day)));
        }

        return points;
    }

    private static List<PageActivity> RankPages(List<EventRow> events, Dictionary<Guid, PageIdentity> visiblePages) =>
        events.GroupBy(e => e.SubjectId)
            .Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => visiblePages[x.Id].Title, StringComparer.OrdinalIgnoreCase)
            .Take(TopN)
            .Select(x => new PageActivity(x.Id, visiblePages[x.Id].Title, visiblePages[x.Id].Slug, visiblePages[x.Id].SpaceKey, x.Count))
            .ToList();

    /// <summary>
    /// Ranked people, named. See PersonActivity's doc for why this is a deliberate
    /// exposure rather than an oversight. Rows with no acting user (a system actor,
    /// §7) are dropped rather than bucketed as "unknown", which would invite reading
    /// the sync CLI as a person.
    /// </summary>
    private async Task<List<PersonActivity>> RankPeopleAsync(List<EventRow> events, CancellationToken cancellationToken)
    {
        var counts = events
            .Where(e => e.UserId is not null)
            .GroupBy(e => e.UserId!.Value)
            .Select(g => (UserId: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(TopN)
            .ToList();
        if (counts.Count == 0)
        {
            return [];
        }

        var ids = counts.Select(c => c.UserId).ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        return counts
            .Select(c => new PersonActivity(c.UserId, names.GetValueOrDefault(c.UserId) ?? "Unknown user", c.Count))
            .ToList();
    }

    private async Task<ContentHealth> BuildHealthAsync(
        Dictionary<Guid, PageIdentity> visiblePages, List<EventRow> views, CancellationToken cancellationToken)
    {
        var ids = visiblePages.Keys.ToList();
        var staleBefore = DateTime.UtcNow - StaleAfter;

        var pages = await db.Pages.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new PageHealthRow(p.Id, p.UpdatedAtUtc, p.ParentPageId))
            .ToListAsync(cancellationToken);

        var labelledIds = await db.PageLabels.AsNoTracking()
            .Where(pl => ids.Contains(pl.PageId))
            .Select(pl => pl.PageId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var stale = pages.Where(p => p.UpdatedAtUtc < staleBefore).ToList();
        var viewedIds = views.Select(v => v.SubjectId).ToHashSet();

        return new ContentHealth(
            StalePageCount: stale.Count,
            StalestPages: stale
                .OrderBy(p => p.UpdatedAtUtc)
                .Take(TopN)
                .Select(p => new PageActivity(
                    p.Id, visiblePages[p.Id].Title, visiblePages[p.Id].Slug, visiblePages[p.Id].SpaceKey,
                    (int)(DateTime.UtcNow - p.UpdatedAtUtc).TotalDays))
                .ToList(),
            // "Orphan" here means top-level, which is the only parentage this data
            // model has. It is a prompt to check filing, not an error.
            OrphanPageCount: pages.Count(p => p.ParentPageId is null),
            UnlabelledPageCount: pages.Count - labelledIds.Count,
            NeverViewedPageCount: pages.Count(p => !viewedIds.Contains(p.Id)));
    }

    /// <summary>
    /// Search terms from `search.query` details, scoped to <paramref name="spaceKeys"/> —
    /// the spaces the caller actually administers, never the spaces they asked about.
    /// A space report counts only searches scoped to that space, because a global
    /// search's terms are not that space's business.
    ///
    /// <para><paramref name="siteWide"/> — instance admin, no space key — is the ONE case
    /// that drops the filter, and it is the only case entitled to: it also picks up
    /// searches that named no space at all (<c>e.SpaceKey == null</c>), which no
    /// per-space filter can ever match. Passing "the caller asked for null" here instead
    /// of "the caller is an instance admin" is what let a space admin read every user's
    /// search terms; see GetReportAsync's note.</para>
    /// </summary>
    private async Task<List<SearchActivity>> BuildSearchesAsync(
        List<string> spaceKeys, bool siteWide, DateTime fromUtc, DateTime toUtc,
        bool zeroResultsOnly, CancellationToken cancellationToken)
    {
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "search.query"
                && e.TimestampUtc >= fromUtc && e.TimestampUtc < toUtc
                && e.Outcome == AuditOutcome.Success
                && e.DetailsJson != null
                && (siteWide || (e.SpaceKey != null && spaceKeys.Contains(e.SpaceKey))))
            .Select(e => e.DetailsJson!)
            .ToListAsync(cancellationToken);

        var parsed = new List<(string Query, bool ZeroResults)>();
        foreach (var json in rows)
        {
            // A details payload that does not parse is skipped rather than fatal: the
            // shape is a convention, not a contract, and one malformed historic row
            // must not take out the whole panel.
            try
            {
                var element = JsonDocument.Parse(json).RootElement;
                if (!element.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = queryElement.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var zero = element.TryGetProperty("resultCount", out var countElement)
                    && countElement.ValueKind == JsonValueKind.Number
                    && countElement.GetInt32() == 0;
                parsed.Add((text.Trim(), zero));
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return parsed
            .Where(p => !zeroResultsOnly || p.ZeroResults)
            .GroupBy(p => p.Query, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SearchActivity(g.Key, g.Count(), g.Count(p => p.ZeroResults)))
            .OrderByDescending(s => s.RunCount)
            .ThenBy(s => s.Query, StringComparer.OrdinalIgnoreCase)
            .Take(TopN)
            .ToList();
    }

    private static AnalyticsReport EmptyReport(AnalyticsScope scope) =>
        new(scope, [], [], [], [], [], new ContentHealth(0, [], 0, 0, 0), [], []);

    private sealed record PageIdentity(string Title, string Slug, string SpaceKey);

    private sealed record EventRow(string Action, Guid SubjectId, Guid? UserId, DateTime TimestampUtc);

    private sealed record PageHealthRow(Guid Id, DateTime UpdatedAtUtc, Guid? ParentPageId);
}
