namespace RocketWiki.Core.Services;

/// <summary>
/// What an analytics report was computed over, carried back so a screen can state
/// its own scope rather than assume it. `SpaceKey` is null for the site-wide report.
/// </summary>
public sealed record AnalyticsScope(string? SpaceKey, DateTime FromUtc, DateTime ToUtc, int VisiblePageCount);

/// <summary>One day's activity. Days with no activity are still present, at zero:
/// a sparse series would draw a chart that lies about the shape of a quiet week.</summary>
public sealed record ActivityPoint(DateOnly Day, int Views, int Edits);

/// <summary>A page and how often something happened to it. `Title` is denormalized at
/// read time from the pages the caller can see, so a page that has since been trashed
/// or become invisible simply does not appear.</summary>
public sealed record PageActivity(Guid PageId, string Title, string Slug, string SpaceKey, int Count);

/// <summary>
/// A named person and their activity count.
///
/// <para>Readers are named here because the instance owner asked for it. That is a
/// policy choice with teeth: design.md §15 calls an unregulated record of
/// who-reads-what exactly the thing telemetry must never become, and this is that
/// record, deliberately, behind an admin gate and behind its own audit row. Reading
/// a report is itself an <c>analytics.view</c> event, so the surveillance surface is
/// on the record rather than silent.</para>
/// </summary>
public sealed record PersonActivity(Guid UserId, string DisplayName, int Count);

/// <summary>
/// What needs attention rather than what is popular. Every count here is over the
/// caller's visible set, so two admins with different grants legitimately see
/// different numbers — that is the §21 gate working, not an inconsistency.
/// </summary>
public sealed record ContentHealth(
    int StalePageCount,
    IReadOnlyList<PageActivity> StalestPages,
    int OrphanPageCount,
    int UnlabelledPageCount,
    int NeverViewedPageCount);

/// <summary>A search term and how often it was run. `ZeroResultCount` is the number of
/// those runs that returned nothing to that searcher — the clearest signal of content
/// people expected to find and did not.</summary>
public sealed record SearchActivity(string Query, int RunCount, int ZeroResultCount);

/// <summary>
/// The whole report. Assembled in one pass so a screen renders one loading state
/// rather than five, and because every panel is filtered by the same visible-page set
/// — computing them separately would invite one of them to forget.
/// </summary>
public sealed record AnalyticsReport(
    AnalyticsScope Scope,
    IReadOnlyList<ActivityPoint> Activity,
    IReadOnlyList<PageActivity> MostViewed,
    IReadOnlyList<PageActivity> MostEdited,
    IReadOnlyList<PersonActivity> TopReaders,
    IReadOnlyList<PersonActivity> TopContributors,
    ContentHealth Health,
    IReadOnlyList<SearchActivity> TopSearches,
    IReadOnlyList<SearchActivity> ZeroResultSearches);
