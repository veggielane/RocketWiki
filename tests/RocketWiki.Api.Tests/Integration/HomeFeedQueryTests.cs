using System.Net.Http.Json;
using System.Reflection;
using HotChocolate.Types;
using RocketWiki.Api.GraphQL;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8's three homepage feeds. All three are <b>cross-space</b> reads, which is
/// what makes permission filtering the feature rather than a refinement: the candidate set
/// is every recent revision / every page you have written on / everything you have opened,
/// drawn from spaces you may have no role in at all. Without canView these are three
/// different ways to enumerate the instance.
///
/// <para>So the tests that matter most are the absence ones, in the shape
/// <c>PageAdversarialLeakTests</c> uses: a restricted page must be absent from every feed
/// by every route into it — as recent activity, as content the caller themselves wrote,
/// and as a page the caller has actually opened. Each of those is a different reason the
/// page would otherwise be in the list, and each is tested separately, because a filter
/// that catches two of the three is the one that ships.</para>
/// </summary>
public sealed class HomeFeedQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string ActivityQuery = """
        { activityFeed(first: 20) { totalCount nodes { revisionNumber isCreate occurredAtUtc
            author { displayName } page { id title spaceKey marking { label } } } } }
        """;

    private const string StaleQuery = """
        { myStaleContent(first: 20) { totalCount nodes { page { id title spaceKey updatedAtUtc } } } }
        """;

    private const string ViewedQuery = """
        { myRecentlyViewed(first: 20) { nodes { lastViewedAtUtc page { id title spaceKey } } } }
        """;

    /// <summary>
    /// The body of the SPA's <c>PageMarking</c> fragment (web/src/graphql/operations/
    /// markings.graphql), spelled out because the paging test below must exercise the
    /// selection the frontend actually sends, not a lighter stand-in.
    /// </summary>
    private const string MarkingFragment = "level levelName eyesOnly ukPrefix label";

    /// <summary>
    /// Seeds a space plus one page, optionally restricted to a nationality the caller
    /// will not hold. The revision is authored by <paramref name="authorId"/> so the
    /// page can be made to look like the caller's own work.
    /// </summary>
    private async Task<(Space Space, Page Page, User Author)> SeedPageAsync(
        string? viewRestrictionNationality = null, Guid? authorId = null, string title = "Feed Page")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var author = await db.Users.FirstOrDefaultAsync(u => u.Id == authorId);
        if (author is null)
        {
            author = new User
            {
                Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder",
                CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(author);
            await db.SaveChangesAsync();
        }

        var space = new Space
        {
            Key = $"FD{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Feed Space", OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = author.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = author.Id,
            UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = author.Id,
        }));

        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = $"p{Guid.NewGuid():N}"[..10],
            Title = title, CurrentRevisionNumber = 1, CurrentContent = "# Body",
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Pages.Add(page);
        db.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id, RevisionNumber = 1, Title = title, Content = "# Body",
            AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow,
        });

        if (viewRestrictionNationality is not null)
        {
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction, PageId = page.Id, Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(
                    new AttrCondition("nationality", [viewRestrictionNationality])),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = author.Id,
                UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = author.Id,
            });
        }

        await db.SaveChangesAsync();
        return (space, page, author);
    }

    private async Task<Guid> LocalUserIdOf(string sub)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return (await db.Users.SingleAsync(u => u.Subject == sub)).Id;
    }

    private static List<string> PageIdsIn(JsonDocument result, string field) =>
        result.RootElement.GetProperty("data").GetProperty(field).GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("page").GetProperty("id").GetString()!)
            .ToList();

    // ---- activityFeed ---------------------------------------------------------------

    [Fact]
    public async Task ActivityFeed_ShowsAViewablePage_WithItsRevisionFacts()
    {
        var (space, page, _) = await SeedPageAsync(title: $"Visible {Guid.NewGuid():N}"[..18]);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");
        using var result = await client.PostGraphQLAsync(ActivityQuery);

        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        var node = Assert.Single(
            result.RootElement.GetProperty("data").GetProperty("activityFeed").GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("page").GetProperty("id").GetString() == page.Id.ToString());

        Assert.Equal(1, node.GetProperty("revisionNumber").GetInt32());
        Assert.True(node.GetProperty("isCreate").GetBoolean());
        Assert.Equal(space.Key, node.GetProperty("page").GetProperty("spaceKey").GetString());

        // §21: the marking arrives through the page resolver, the one gated rendering.
        Assert.False(string.IsNullOrWhiteSpace(
            node.GetProperty("page").GetProperty("marking").GetProperty("label").GetString()));
    }

    [Fact]
    public async Task ActivityFeed_OmitsARestrictedPage_Entirely()
    {
        // The adversarial case. The page is recent activity in a space this caller can
        // otherwise browse — only the page restriction stands between them and it.
        var (_, restricted, _) = await SeedPageAsync(viewRestrictionNationality: "US", title: "ZZRESTRICTEDZZ");

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);
        using var result = await client.PostGraphQLAsync(ActivityQuery);

        Assert.DoesNotContain(restricted.Id.ToString(), PageIdsIn(result, "activityFeed"));

        // §6.7: absent, and absent without trace — no title, no count of what was pruned.
        Assert.DoesNotContain("ZZRESTRICTEDZZ", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityFeed_IsEmptyForAnonymous_RatherThanErroring()
    {
        var anonymous = factory.CreateClient();
        using var result = await anonymous.PostGraphQLAsync(ActivityQuery);

        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        Assert.Empty(result.RootElement.GetProperty("data").GetProperty("activityFeed")
            .GetProperty("nodes").EnumerateArray());
    }

    // ---- myStaleContent -------------------------------------------------------------

    [Fact]
    public async Task MyStaleContent_ShowsPagesTheCallerAuthored_StalestFirst()
    {
        var sub = $"author-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);

        // Provision the local user by making any authenticated call first.
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var userId = await LocalUserIdOf(sub);

        var (_, older, _) = await SeedPageAsync(authorId: userId, title: "Older");
        var (_, newer, _) = await SeedPageAsync(authorId: userId, title: "Newer");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var olderRow = await db.Pages.SingleAsync(p => p.Id == older.Id);
            olderRow.UpdatedAtUtc = DateTime.UtcNow.AddDays(-90);
            await db.SaveChangesAsync();
        }

        using var result = await client.PostGraphQLAsync(StaleQuery);

        var ids = PageIdsIn(result, "myStaleContent");
        Assert.Contains(older.Id.ToString(), ids);
        Assert.Contains(newer.Id.ToString(), ids);

        // Oldest first — the stalest surface at the top is the point of the feed.
        Assert.True(ids.IndexOf(older.Id.ToString()) < ids.IndexOf(newer.Id.ToString()));
    }

    [Fact]
    public async Task MyStaleContent_RanksByAnyonesLastEdit_NotByMine()
    {
        // The distinction the whole ordering rests on. The timings are chosen so the two
        // candidate sort keys give OPPOSITE answers — anything less does not discriminate:
        //
        //             my last edit    anyone's last edit (page.UpdatedAtUtc)
        //   abandoned    -100d           -100d      <- staler by the rule we want
        //   maintained   -300d            today
        //
        // Ordering by page.UpdatedAtUtc puts `abandoned` first; ordering by my own last
        // edit puts `maintained` first. Making my edits merely EQUAL would let the
        // ThenBy(p.Id) tiebreak decide by GUID and the test would pass under both rules —
        // which is what a first draft of this test did.
        var sub = $"shared-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var userId = await LocalUserIdOf(sub);

        var (_, abandoned, _) = await SeedPageAsync(authorId: userId, title: "Abandoned");
        var (_, maintained, _) = await SeedPageAsync(authorId: userId, title: "Maintained");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

            // I touched `abandoned` MORE recently than `maintained` — and nobody has
            // touched it since, so it is nonetheless the stale one.
            var abandonedRevision = await db.PageRevisions.SingleAsync(r => r.PageId == abandoned.Id);
            abandonedRevision.CreatedAtUtc = DateTime.UtcNow.AddDays(-100);
            (await db.Pages.SingleAsync(p => p.Id == abandoned.Id)).UpdatedAtUtc =
                DateTime.UtcNow.AddDays(-100);

            var maintainedRevision = await db.PageRevisions.SingleAsync(r => r.PageId == maintained.Id);
            maintainedRevision.CreatedAtUtc = DateTime.UtcNow.AddDays(-300);

            // Someone else has kept this one current since.
            var other = new User
            {
                Subject = $"maintainer-{Guid.NewGuid()}", DisplayName = "Maintainer",
                CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(other);

            var maintainedPage = await db.Pages.SingleAsync(p => p.Id == maintained.Id);
            maintainedPage.UpdatedAtUtc = DateTime.UtcNow;
            maintainedPage.CurrentRevisionNumber = 2;
            db.PageRevisions.Add(new PageRevision
            {
                PageId = maintained.Id, RevisionNumber = 2, Title = "Maintained",
                Content = "# Body", AuthorUserId = other.Id, CreatedAtUtc = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        using var result = await client.PostGraphQLAsync(StaleQuery);

        var ids = PageIdsIn(result, "myStaleContent");
        Assert.Contains(maintained.Id.ToString(), ids); // still mine — membership is unchanged
        Assert.True(
            ids.IndexOf(abandoned.Id.ToString()) < ids.IndexOf(maintained.Id.ToString()),
            "a page someone else has maintained must rank below one nobody has touched");
    }

    [Fact]
    public async Task MyStaleContent_CountsWhatIsShown_NotWhatWasWithheld()
    {
        // §6.7 forbids counting what was WITHHELD. This count is taken after filtering, so
        // it must equal the rows actually returned — a count that included the restricted
        // page would be exactly the leak the rule exists to prevent.
        var sub = $"counter-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, nationality: ["NZ"]);
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var userId = await LocalUserIdOf(sub);

        await SeedPageAsync(authorId: userId, title: "Mine And Visible");
        await SeedPageAsync(viewRestrictionNationality: "US", authorId: userId, title: "ZZCOUNTEDBUTHIDDENZZ");

        using var result = await client.PostGraphQLAsync(StaleQuery);

        var feed = result.RootElement.GetProperty("data").GetProperty("myStaleContent");

        // This caller authored exactly these two pages and nothing else in the instance, so
        // the expected count is exact rather than relative. Asserting the literal 1 is what
        // makes this a leak detector: a totalCount taken BEFORE canView would read 2 while
        // returning one row, which is precisely "a count of what was withheld". Comparing
        // totalCount to nodes.Length instead would pass in that case too — the count is
        // derived from the returned list — so it would prove nothing.
        Assert.Equal(1, feed.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, feed.GetProperty("nodes").GetArrayLength());
        Assert.DoesNotContain("ZZCOUNTEDBUTHIDDENZZ", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MyStaleContent_DropsAPageTheAuthorCanNoLongerView()
    {
        // Authorship is not a standing claim on content: canView is re-checked now, not
        // inherited from whenever the page was written (§6.7).
        var sub = $"exauthor-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, nationality: ["NZ"]);
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var userId = await LocalUserIdOf(sub);

        var (_, page, _) = await SeedPageAsync(
            viewRestrictionNationality: "US", authorId: userId, title: "ZZLOSTACCESSZZ");

        using var result = await client.PostGraphQLAsync(StaleQuery);

        Assert.DoesNotContain(page.Id.ToString(), PageIdsIn(result, "myStaleContent"));
        Assert.DoesNotContain("ZZLOSTACCESSZZ", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    // ---- myRecentlyViewed -----------------------------------------------------------

    [Fact]
    public async Task MyRecentlyViewed_ShowsAPageTheCallerOpened_OncePerPage()
    {
        var sub = $"reader-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);

        var (_, page, _) = await SeedPageAsync(title: "Read Me");

        // Two views of one page: the feed is distinct by page, newest view kept.
        using (await client.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { title } }""")) { }
        using (await client.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { title } }""")) { }

        using var result = await client.PostGraphQLAsync(ViewedQuery);

        var ids = PageIdsIn(result, "myRecentlyViewed");
        Assert.Single(ids, id => id == page.Id.ToString());
    }

    [Fact]
    public async Task MyRecentlyViewed_DropsAPageTheCallerHasSinceLostAccessTo()
    {
        // The sharpest of the three: the caller demonstrably DID view this page, and the
        // audit row proving it is still there. Trusting that row would resurface a title
        // the caller can no longer see, which is why the feed re-checks canView now
        // rather than reading history as permission.
        var sub = $"lost-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, nationality: ["NZ"]);

        var (_, page, author) = await SeedPageAsync(title: "ZZWASVIEWABLEZZ");
        using (await client.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { title } }""")) { }

        // Confirm the view really was recorded — otherwise the absence below proves nothing.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var userId = (await db.Users.SingleAsync(u => u.Subject == sub)).Id;
            Assert.True(await db.AuditEvents.AnyAsync(e =>
                e.UserId == userId && e.Action == "page.view" && e.SubjectId == page.Id));

            // Now restrict it to a nationality this caller does not hold.
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction, PageId = page.Id, Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = author.Id,
                UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = author.Id,
            });
            await db.SaveChangesAsync();
        }

        using var result = await client.PostGraphQLAsync(ViewedQuery);

        Assert.DoesNotContain(page.Id.ToString(), PageIdsIn(result, "myRecentlyViewed"));
        Assert.DoesNotContain("ZZWASVIEWABLEZZ", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MyRecentlyViewed_ShowsOnlyTheCallersOwnViews()
    {
        // These rows are the audit log, so "my views" must mean mine. A feed that leaked
        // across users would turn the compliance record into a surveillance surface.
        var (_, page, _) = await SeedPageAsync(title: "Someone Elses Read");

        var other = factory.CreateClient();
        other.SetTestUser(sub: $"other-{Guid.NewGuid()}");
        using (await other.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { title } }""")) { }

        var me = factory.CreateClient();
        me.SetTestUser(sub: $"me-{Guid.NewGuid()}");
        using var result = await me.PostGraphQLAsync(ViewedQuery);

        Assert.DoesNotContain(page.Id.ToString(), PageIdsIn(result, "myRecentlyViewed"));
    }

    // ---- paging ---------------------------------------------------------------------

    /// <summary>
    /// <b>The page size a feed advertises must be one a caller can actually request.</b>
    /// This is the audit-log defect generalised, and it is worth stating why an ordinary
    /// resolver test cannot catch it: both limits that can reject a page size —
    /// <c>MaxPageSize</c> and the field-cost budget — are enforced during <b>validation</b>,
    /// before any resolver runs. The rejection is an HTTP 400 with no <c>data</c> at all,
    /// so a test that asserts on results, or that goes through a helper calling
    /// <c>EnsureSuccessStatusCode</c>, reports a transport failure rather than the defect.
    /// Hence the raw status assertion here.
    ///
    /// <para><b>The selections below are the exact ones the homepage sends</b>, agreed with
    /// the frontend rather than invented here — cost is <c>first × row selection</c>, so a
    /// page size is only ever safe with respect to a particular selection, and a lighter
    /// stand-in would prove nothing about what users actually run. Trimming these to make
    /// the test pass defeats it.</para>
    ///
    /// <para>The budget is tight on purpose (task #38 covers making it deliberate). Only
    /// <c>page</c>, <c>spaceKey</c>, <c>marking</c> and <c>author</c> are weighted — 10
    /// each; <c>id</c>, <c>title</c>, <c>slug</c>, <c>updatedAtUtc</c> and <c>icon</c> (an
    /// enum) are free. That is 40 per activity row and 30 per row elsewhere, so 20 rows
    /// costs 800/600 against a 1000 budget. <b>One more weighted field on a feed row breaks
    /// it</b> — measured, not assumed: adding <c>page { labels }</c> (weight 10) to the
    /// activity row takes it to 1041 and this test fails, which is the point. It fails here
    /// rather than 400ing somebody's homepage.</para>
    /// </summary>
    [Theory]
    [InlineData("activityFeed", "totalCount",
        "revisionNumber isCreate occurredAtUtc author { id displayName hasAvatar } "
        + "page { id title spaceKey slug icon marking { " + MarkingFragment + " } }")]
    [InlineData("myStaleContent", "totalCount",
        "page { id title spaceKey slug icon updatedAtUtc marking { " + MarkingFragment + " } }")]
    [InlineData("myRecentlyViewed", "",
        "lastViewedAtUtc page { id title spaceKey slug icon marking { " + MarkingFragment + " } }")]
    public async Task EveryFeed_AcceptsItsOwnAdvertisedPageSize(
        string field, string connectionSelection, string rowSelection)
    {
        // Read the advertised maximum off the resolver rather than restating it, so that
        // raising MaxPageSize without re-measuring the cost budget fails here.
        var advertised = AdvertisedMaxPageSize(field);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"pager-{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync(
            "/graphql",
            new { query = $"{{ {field}(first: {advertised}) {{ {connectionSelection} nodes {{ {rowSelection} }} }} }}" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"{field}(first: {advertised}) was rejected before reaching the resolver: {body}");

        using var parsed = JsonDocument.Parse(body);
        Assert.False(
            parsed.RootElement.TryGetProperty("errors", out var errors),
            $"{field}(first: {advertised}) returned errors: {errors}");
    }

    /// <summary>
    /// The page size a feed advertises, read off its own <c>[UsePaging]</c> attribute so
    /// the test cannot drift from the server: raising the cap re-points this test at the
    /// new number rather than leaving it quietly asserting the old one.
    /// </summary>
    private static int AdvertisedMaxPageSize(string field)
    {
        var resolver = typeof(Query).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => string.Equals(m.Name, field, StringComparison.OrdinalIgnoreCase));

        return resolver.GetCustomAttribute<UsePagingAttribute>()?.MaxPageSize
            ?? throw new InvalidOperationException($"{field} declares no [UsePaging] MaxPageSize");
    }
}