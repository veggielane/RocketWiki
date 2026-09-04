using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Analytics over the audit trail. The panels are ordinary aggregation; what these
/// tests are really for is the property underneath them — <b>every number is counted
/// over the caller's own visible set</b>, so a report can never disclose the existence
/// of content the reader could not open for themselves.
///
/// <para>That matters more here than almost anywhere else in the product. A chart is
/// patient: it is looked at repeatedly, it invites comparison across periods, and
/// nobody checks its arithmetic by hand. design.md refuses classification-keyed
/// aggregates in three places for this reason, and an admin gate does not change it —
/// §21's clearance gate subtracts from instance admins too.</para>
/// </summary>
public class AnalyticsServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");
    private static readonly DateTime From = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Principal Caller(params string[] groups) => Principal.Create("caller-sub", groups);

    private static async Task GrantAsync(
        RocketWikiDbContext context, Guid spaceId, SpaceRole? role, Guid actingUserId, string expression = """{ "everyone": true }""")
    {
        await context.SaveChangesAsync();
        var result = await new AccessRuleService(context).CreateAsync(
            new CreateAccessRuleRequest(role is null ? AccessRuleKind.AccessGrant : AccessRuleKind.RoleGrant, spaceId, null, role, null, expression),
            Principal.Create("bootstrap", []), isInstanceAdmin: true, actingUserId, AuditCtx);
        Assert.True(result.IsSuccess, $"grant failed: {result.Error}");

        // A role confers no visibility (design.md §6.4); the access grant sits beside it.
        if (role is not null)
        {
            var access = await new AccessRuleService(context).CreateAsync(
                new CreateAccessRuleRequest(AccessRuleKind.AccessGrant, spaceId, null, null, null, expression),
                Principal.Create("bootstrap", []), isInstanceAdmin: true, actingUserId, AuditCtx);
            Assert.True(access.IsSuccess, $"access grant failed: {access.Error}");
        }
    }

    private static void RecordView(RocketWikiDbContext context, Guid pageId, Guid userId, DateTime at, string action = "page.view") =>
        context.AuditEvents.Add(new AuditEvent
        {
            TimestampUtc = at,
            UserId = userId,
            Action = action,
            SubjectType = AuditSubjectType.Page,
            SubjectId = pageId,
            Outcome = AuditOutcome.Success,
            Channel = AuditChannel.GraphQl,
            RequestId = "req",
            ClientIp = "127.0.0.1",
        });

    /// <summary>A `search.query` row in the shape Query.Search writes (design.md §7):
    /// raw query text and result count in Details, the searched space in SpaceKey — or
    /// null SpaceKey for a search that named no space at all.</summary>
    private static void RecordSearch(
        RocketWikiDbContext context, string? spaceKey, string query, int resultCount, Guid userId, DateTime at) =>
        context.AuditEvents.Add(new AuditEvent
        {
            TimestampUtc = at,
            UserId = userId,
            Action = "search.query",
            Outcome = AuditOutcome.Success,
            Channel = AuditChannel.GraphQl,
            SpaceKey = spaceKey,
            RequestId = "req",
            ClientIp = "127.0.0.1",
            DetailsJson = $$"""{ "query": "{{query}}", "resultCount": {{resultCount}} }""",
        });

    private AnalyticsService NewService(RocketWikiDbContext context) =>
        new(context, new PageReadService(context));

    [Fact]
    public async Task Report_CountsViewsAndNamesReaders()
    {
        var actor = TestData.NewUser("Ada Lovelace");
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantAsync(context, space.Id, SpaceRole.SpaceAdmin, actor.Id);
        var page = TestData.NewPage(space, "runbook");
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Official));
        RecordView(context, page.Id, actor.Id, From.AddDays(2));
        RecordView(context, page.Id, actor.Id, From.AddDays(3));
        context.SaveChanges();

        var result = await NewService(context).GetReportAsync(
            space.Key, From, To, Caller(), isInstanceAdmin: false);

        var report = Assert.IsType<ReadResult<AnalyticsReport>.Found>(result).Value;
        Assert.Equal(2, report.MostViewed.Single().Count);
        Assert.Equal("runbook", report.MostViewed.Single().Slug);
        // The instance owner asked for named readers; this is that choice, pinned.
        Assert.Equal("Ada Lovelace", report.TopReaders.Single().DisplayName);
        Assert.Equal(2, report.TopReaders.Single().Count);
    }

    [Fact]
    public async Task Report_CountsNothingAboutAPageTheCallerCannotRead()
    {
        // THE test. A page the caller is not granted contributes no view count, no ranked
        // row, and nothing to visiblePageCount — because a total that moved when
        // compartmented content was viewed would report the existence of that content to
        // someone §21 has already decided must not learn it. The SECRET level is on the
        // page and gates nobody; the APPLE selector no grant confers is what does.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantAsync(context, space.Id, SpaceRole.SpaceAdmin, actor.Id);

        var readable = TestData.NewPage(space, "readable");
        var secret = TestData.NewPage(space, "secret");
        context.Pages.AddRange(readable, secret);
        context.PageMarkings.Add(TestData.NewMarking(readable, ClassificationLevel.Official));
        context.PageMarkings.Add(TestData.NewMarking(secret, ClassificationLevel.Secret)
            .WithSelectors(RocketWiki.Core.Tests.Access.TestCatalogs.Apple));
        RecordView(context, readable.Id, actor.Id, From.AddDays(1));
        for (var i = 0; i < 50; i++)
        {
            RecordView(context, secret.Id, actor.Id, From.AddDays(1));
        }

        context.SaveChanges();

        // The caller administers the space and is granted no selector: cannot read the APPLE page.
        var result = await NewService(context).GetReportAsync(
            space.Key, From, To, Caller(), isInstanceAdmin: false);

        var report = Assert.IsType<ReadResult<AnalyticsReport>.Found>(result).Value;
        Assert.Equal(1, report.Scope.VisiblePageCount);
        var ranked = Assert.Single(report.MostViewed);
        Assert.Equal("readable", ranked.Slug);
        Assert.Equal(1, ranked.Count);
        // Fifty views of a page they cannot open must not show up as reader activity
        // either — the total would be the leak just as surely as the page name.
        Assert.Equal(1, report.TopReaders.Single().Count);
    }

    [Fact]
    public async Task Report_ForACallerWhoAdministersNothing_IsDenied()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        // A viewer, not an admin: they can read the space and still get no report.
        await GrantAsync(context, space.Id, null, actor.Id);
        context.SaveChanges();

        var result = await NewService(context).GetReportAsync(
            space.Key, From, To, Caller(), isInstanceAdmin: false);

        Assert.IsType<ReadResult<AnalyticsReport>.Denied>(result);
    }

    [Fact]
    public async Task Report_ForAnUnknownSpace_IsRefusedTheSameWayAsOneNotAdministered()
    {
        // §6.7 at the report boundary: a key that does not exist and a key that is not
        // yours produce the same outcome, so this cannot enumerate space keys.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantAsync(context, space.Id, SpaceRole.SpaceAdmin, actor.Id);
        context.SaveChanges();

        var unknown = await NewService(context).GetReportAsync(
            "NOSUCHSPACE", From, To, Caller(), isInstanceAdmin: false);

        Assert.IsType<ReadResult<AnalyticsReport>.Denied>(unknown);
    }

    /// <summary>
    /// A null <c>spaceKey</c> means different things to the two callers who may pass it,
    /// and the difference is the point: for an instance admin it is genuinely site-wide,
    /// for a space admin it means "every space I administer". Every panel has to honour
    /// that distinction — including the search panels, which read `search.query` audit
    /// rows rather than the visible page set and so have no structural scoping of their
    /// own (design.md §7 reserves the audit log to instance admins).
    ///
    /// <para>The previous version of this test was named for the invariant and only
    /// checked the page panel, which is safe by construction; the search panel, which
    /// was not, went unasserted and leaked every user's search terms to any space
    /// admin who passed a null key.</para>
    /// </summary>
    [Fact]
    public async Task Report_SiteWide_ForASpaceAdmin_ScopesEveryPanelToTheSpacesTheyAdminister()
    {
        var actor = TestData.NewUser();
        var eng = TestData.NewSpace();
        var ops = TestData.NewSpace("OPS");
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.AddRange(eng, ops);
        await GrantAsync(context, eng.Id, SpaceRole.SpaceAdmin, actor.Id);
        await GrantAsync(context, ops.Id, null, actor.Id);

        foreach (var (space, slug) in new[] { (eng, "eng-page"), (ops, "ops-page") })
        {
            var page = TestData.NewPage(space, slug);
            context.Pages.Add(page);
            context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Official));
            RecordView(context, page.Id, actor.Id, From.AddDays(1));
        }

        // Three searches by somebody else: one in the space this caller administers, one
        // in a space they merely view, and one that named no space at all.
        RecordSearch(context, eng.Key, "eng-term", 2, actor.Id, From.AddDays(1));
        RecordSearch(context, ops.Key, "ops-term", 0, actor.Id, From.AddDays(1));
        RecordSearch(context, null, "global-term", 0, actor.Id, From.AddDays(1));

        context.SaveChanges();
        var service = NewService(context);

        // Space admin of ENG only: a site-wide request reports ENG, never OPS.
        var scoped = Assert.IsType<ReadResult<AnalyticsReport>.Found>(
            await service.GetReportAsync(null, From, To, Caller(), isInstanceAdmin: false)).Value;
        Assert.Equal("eng-page", Assert.Single(scoped.MostViewed).Slug);

        // The search panels are scoped the same way. A space admin learns nothing about
        // what was searched for in a space they do not administer, and nothing about
        // searches that named no space — both are somebody else's business (§7/§15).
        Assert.Equal("eng-term", Assert.Single(scoped.TopSearches).Query);
        Assert.Empty(scoped.ZeroResultSearches);

        // Instance admin: every space, and the unscoped search too.
        var siteWide = Assert.IsType<ReadResult<AnalyticsReport>.Found>(
            await service.GetReportAsync(null, From, To, Caller(), isInstanceAdmin: true)).Value;
        Assert.Equal(2, siteWide.MostViewed.Count);
        Assert.Equal(3, siteWide.TopSearches.Count);
        Assert.Equal(
            new[] { "global-term", "ops-term" },
            siteWide.ZeroResultSearches.Select(s => s.Query).OrderBy(q => q, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Report_ActivitySeries_HasAPointForEveryDayIncludingQuietOnes()
    {
        // A sparse series drawn as a line implies activity between the points it has,
        // which is exactly backwards for a quiet week.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantAsync(context, space.Id, SpaceRole.SpaceAdmin, actor.Id);
        var page = TestData.NewPage(space, "runbook");
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Official));
        RecordView(context, page.Id, actor.Id, From.AddDays(4));
        context.SaveChanges();

        var report = Assert.IsType<ReadResult<AnalyticsReport>.Found>(
            await NewService(context).GetReportAsync(space.Key, From, From.AddDays(7), Caller(), isInstanceAdmin: false)).Value;

        Assert.Equal(7, report.Activity.Count);
        Assert.Equal(1, report.Activity.Single(p => p.Day == DateOnly.FromDateTime(From.AddDays(4))).Views);
        Assert.Equal(6, report.Activity.Count(p => p.Views == 0));
    }

    [Fact]
    public async Task Report_SurfacesSearchesThatFoundNothing()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantAsync(context, space.Id, SpaceRole.SpaceAdmin, actor.Id);
        var page = TestData.NewPage(space, "runbook");
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Official));

        foreach (var (query, count) in new[] { ("onboarding", 0), ("onboarding", 0), ("runbook", 3) })
        {
            context.AuditEvents.Add(new AuditEvent
            {
                TimestampUtc = From.AddDays(1),
                UserId = actor.Id,
                Action = "search.query",
                Outcome = AuditOutcome.Success,
                Channel = AuditChannel.GraphQl,
                SpaceKey = space.Key,
                RequestId = "req",
                ClientIp = "127.0.0.1",
                DetailsJson = $$"""{ "query": "{{query}}", "resultCount": {{count}} }""",
            });
        }

        context.SaveChanges();

        var report = Assert.IsType<ReadResult<AnalyticsReport>.Found>(
            await NewService(context).GetReportAsync(space.Key, From, To, Caller(), isInstanceAdmin: false)).Value;

        Assert.Equal(2, report.TopSearches.Count);
        var empty = Assert.Single(report.ZeroResultSearches);
        Assert.Equal("onboarding", empty.Query);
        Assert.Equal(2, empty.RunCount);
    }

    [Fact]
    public async Task Report_SkipsAMalformedSearchDetailRowRatherThanLosingThePanel()
    {
        // The details payload is a convention, not a contract; one bad historic row
        // must not take out the whole panel for everyone.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantAsync(context, space.Id, SpaceRole.SpaceAdmin, actor.Id);
        var page = TestData.NewPage(space, "runbook");
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Official));

        foreach (var details in new[] { "not json at all", """{ "noQueryHere": 1 }""", """{ "query": "good", "resultCount": 1 }""" })
        {
            context.AuditEvents.Add(new AuditEvent
            {
                TimestampUtc = From.AddDays(1),
                UserId = actor.Id,
                Action = "search.query",
                Outcome = AuditOutcome.Success,
                Channel = AuditChannel.GraphQl,
                SpaceKey = space.Key,
                RequestId = "req",
                ClientIp = "127.0.0.1",
                DetailsJson = details,
            });
        }

        context.SaveChanges();

        var report = Assert.IsType<ReadResult<AnalyticsReport>.Found>(
            await NewService(context).GetReportAsync(space.Key, From, To, Caller(), isInstanceAdmin: false)).Value;

        Assert.Equal("good", Assert.Single(report.TopSearches).Query);
    }
}
