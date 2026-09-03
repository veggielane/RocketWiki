using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The `search` root field end to end (design.md §6.7/§7/§8/§9, milestone 4):
/// contract shape as the shipped SPA operation expects it
/// (web/src/graphql/operations/search.graphql), permission filtering proven
/// adversarially at the API boundary, positional-cursor pagination, and the
/// search.query audit row with the query text in Details. The factory is shared
/// per class, so every test seeds its own space with unique keys and searches
/// for its own unique terms — no cross-test bleed through the shared SQLite file.
/// </summary>
public sealed class SearchQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Space Space, User Creator)> SeedSpaceAsync(string? grantExpressionJson = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"seed-{Guid.NewGuid():N}",
            DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);

        var space = new Space
        {
            Key = $"S{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Search Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = grantExpressionJson ?? RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        await db.SaveChangesAsync();
        return (space, creator);
    }

    private async Task<Page> SeedPageAsync(
        Space space, string slug, string title, string content, DateTime? updatedAtUtc = null, string? restrictToGroup = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = slug,
            Title = title,
            CurrentContent = content,
            CreatedAtUtc = now,
            UpdatedAtUtc = updatedAtUtc ?? now,
        };
        db.Pages.Add(page);

        if (restrictToGroup is not null)
        {
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction,
                PageId = page.Id,
                Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition(restrictToGroup)),
                CreatedAtUtc = now,
                CreatedByUserId = space.CreatedByUserId,
                UpdatedAtUtc = now,
                UpdatedByUserId = space.CreatedByUserId,
            });
        }

        await db.SaveChangesAsync();
        return page;
    }

    private HttpClient CreateUserClient(params string[] groups)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"searcher-{Guid.NewGuid():N}", email: "s@example.test", name: "Searcher", groups: groups);
        return client;
    }

    [Fact]
    public async Task Search_ReturnsTheContractShapeTheSpaOperationExpects()
    {
        var (space, _) = await SeedSpaceAsync();
        var page = await SeedPageAsync(space, "engines", "Engine Handbook",
            "# Engines\n\nintro\n\n## Turbopumps\n\nabout impeller-cavitation margins");

        var client = CreateUserClient();
        // Field-for-field the SPA's SearchPages operation
        // (web/src/graphql/operations/search.graphql) with its variables inlined.
        using var response = await client.PostGraphQLAsync("""
            query SearchPages {
              search(query: "impeller-cavitation") {
                totalCount
                pageInfo { hasNextPage endCursor }
                edges { cursor node { snippet headingPath anchorId page { id title spaceKey } } }
              }
            }
            """);

        var search = response.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(1, search.GetProperty("totalCount").GetInt32());
        Assert.False(search.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var edge = Assert.Single(search.GetProperty("edges").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("cursor").GetString()));

        var node = edge.GetProperty("node");
        Assert.Contains("impeller-cavitation", node.GetProperty("snippet").GetString());
        Assert.Equal(["Engines", "Turbopumps"],
            node.GetProperty("headingPath").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("engines--turbopumps", node.GetProperty("anchorId").GetString());

        var pageNode = node.GetProperty("page");
        Assert.Equal(page.Id.ToString(), pageNode.GetProperty("id").GetString());
        Assert.Equal("Engine Handbook", pageNode.GetProperty("title").GetString());
        Assert.Equal(space.Key, pageNode.GetProperty("spaceKey").GetString());
    }

    [Fact]
    public async Task Search_RestrictedPage_AbsentFromResults_ByTitleMatchAndByContentMatch()
    {
        // design.md §6.7: adversarial absence. The restricted page matches the search
        // BOTH ways a page can match - by title and by content - and the caller holds
        // the space grant (so the page would otherwise be a hit). Not only must its
        // hit be missing while the permitted sibling's is present: the entire
        // response body must not contain its title or any fragment of its content.
        var (space, _) = await SeedSpaceAsync();
        var visible = await SeedPageAsync(space, "public", "Public flange-torque notes",
            "flange-torque values for ground ops");
        await SeedPageAsync(space, "secret", "Restricted flange-torque overrides",
            "flange-torque overrides for the classified-vehicle",
            restrictToGroup: "top-secret");

        var client = CreateUserClient(); // no groups - fails the restriction, passes the grant
        using var response = await client.PostGraphQLAsync("""
            query { search(query: "flange-torque") { totalCount edges { node { snippet headingPath anchorId page { id title } } } } }
            """);

        var search = response.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(1, search.GetProperty("totalCount").GetInt32());

        var edge = Assert.Single(search.GetProperty("edges").EnumerateArray());
        Assert.Equal(visible.Id.ToString(), edge.GetProperty("node").GetProperty("page").GetProperty("id").GetString());

        var rawResponse = response.RootElement.GetRawText();
        Assert.DoesNotContain("Restricted flange-torque overrides", rawResponse);
        Assert.DoesNotContain("classified-vehicle", rawResponse);

        // And the caller who satisfies the restriction sees both - proving the page
        // was hidden by the rule engine, not by the search simply missing it.
        var clearedClient = CreateUserClient("top-secret");
        using var clearedResponse = await clearedClient.PostGraphQLAsync("""
            query { search(query: "flange-torque") { totalCount } }
            """);
        Assert.Equal(2, clearedResponse.RootElement.GetProperty("data").GetProperty("search")
            .GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Search_Pagination_TwentyPerPage_CursorsAdvanceWithoutOverlapOrGaps()
    {
        var (space, _) = await SeedSpaceAsync();
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < 25; i++)
        {
            // Distinct UpdatedAtUtc so the LIKE tier's recency ordering is total.
            await SeedPageAsync(space, $"page-{i}", $"Gimbal page {i}",
                $"gimbal-actuator notes {i}", baseTime.AddMinutes(-i));
        }

        var client = CreateUserClient();
        using var firstPage = await client.PostGraphQLAsync("""
            query { search(query: "gimbal-actuator") { totalCount pageInfo { hasNextPage endCursor } edges { cursor node { page { id } } } } }
            """);

        var first = firstPage.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(25, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, first.GetProperty("edges").GetArrayLength());
        Assert.True(first.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        var endCursor = first.GetProperty("pageInfo").GetProperty("endCursor").GetString();
        Assert.False(string.IsNullOrEmpty(endCursor));

        using var secondPage = await client.PostGraphQLAsync($$"""
            query { search(query: "gimbal-actuator", after: "{{endCursor}}") { totalCount pageInfo { hasNextPage endCursor } edges { cursor node { page { id } } } } }
            """);

        var second = secondPage.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(25, second.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, second.GetProperty("edges").GetArrayLength());
        Assert.False(second.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        static IEnumerable<string> PageIds(JsonElement search) =>
            search.GetProperty("edges").EnumerateArray()
                .Select(e => e.GetProperty("node").GetProperty("page").GetProperty("id").GetString()!);

        var firstIds = PageIds(first).ToHashSet();
        var secondIds = PageIds(second).ToHashSet();
        Assert.Equal(20, firstIds.Count);
        Assert.Equal(5, secondIds.Count);
        Assert.Empty(firstIds.Intersect(secondIds)); // no overlap
        Assert.Equal(25, firstIds.Union(secondIds).Count()); // no gaps: every seeded page shows exactly once
    }

    /// <summary>
    /// <c>first</c> exists so the SPA can put pagination in the URL. <c>after</c> alone
    /// restores a POSITION but not how many rows preceded it, so a restored page could not
    /// be refetched from cold; with both, a URL carrying (after, first) is self-sufficient.
    ///
    /// <para>Asserted as the round trip that actually matters — page 2 fetched with an
    /// explicit size lands on the same rows as walking there — because a size that is
    /// honoured for the slice but not for the cursor arithmetic would look right on page 1
    /// and silently skip or repeat rows on page 2.</para>
    /// </summary>
    [Fact]
    public async Task Search_WithAnExplicitFirst_PagesByThatSize_AndTheCursorsStayConsistent()
    {
        var (space, _) = await SeedSpaceAsync();
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < 12; i++)
        {
            await SeedPageAsync(space, $"sized-{i}", $"Sized page {i}",
                $"turbopump-seal notes {i}", baseTime.AddMinutes(-i));
        }

        var client = CreateUserClient();

        using var page1 = await client.PostGraphQLAsync("""
            query { search(query: "turbopump-seal", first: 5) { totalCount pageInfo { hasNextPage endCursor } edges { node { page { id } } } } }
            """);
        var first = page1.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(12, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, first.GetProperty("edges").GetArrayLength());
        Assert.True(first.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var cursor = first.GetProperty("pageInfo").GetProperty("endCursor").GetString();
        using var page2 = await client.PostGraphQLAsync($$"""
            query { search(query: "turbopump-seal", after: "{{cursor}}", first: 5) { pageInfo { hasNextPage } edges { node { page { id } } } } }
            """);
        var second = page2.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(5, second.GetProperty("edges").GetArrayLength());
        Assert.True(second.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        static IEnumerable<string> PageIds(JsonElement search) =>
            search.GetProperty("edges").EnumerateArray()
                .Select(e => e.GetProperty("node").GetProperty("page").GetProperty("id").GetString()!);

        Assert.Empty(PageIds(first).Intersect(PageIds(second)));
        Assert.Equal(10, PageIds(first).Union(PageIds(second)).Count());
    }

    /// <summary>
    /// Omitting <c>first</c> keeps the shipped default, and out-of-range values clamp
    /// rather than erroring — the ceiling is the number of hits the resolver materializes
    /// at all, so a larger ask could not be honoured even in principle, and 0 would return
    /// an empty page indistinguishable from "no results".
    /// </summary>
    [Theory]
    [InlineData(null, 20)]   // shipped default, unchanged
    [InlineData(0, 1)]       // clamps up rather than answering an empty page
    [InlineData(-5, 1)]
    [InlineData(500, 22)]    // clamps to the materialization ceiling; only 22 hits exist
    public async Task Search_FirstIsClampedRatherThanRefused(int? first, int expectedEdges)
    {
        // The factory is a class fixture, so every theory row shares one database — a
        // shared term would accumulate the previous rows' pages and the counts would
        // drift upward row by row.
        var term = $"zzclamp{Guid.NewGuid():N}"[..16];
        var (space, _) = await SeedSpaceAsync();
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < 22; i++)
        {
            await SeedPageAsync(space, $"clamped-{term}-{i}", $"Clamped page {i}",
                $"{term} notes {i}", baseTime.AddMinutes(-i));
        }

        var argument = first is null ? string.Empty : $", first: {first}";
        using var result = await CreateUserClient().PostGraphQLAsync($$"""
            query { search(query: "{{term}}"{{argument}}) { totalCount edges { node { page { id } } } } }
            """);

        var search = result.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(22, search.GetProperty("totalCount").GetInt32());
        Assert.Equal(expectedEdges, search.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task Search_WritesExactlyOneAuditRow_WithTheQueryTextInDetails()
    {
        // design.md §7: the event model's Details column explicitly includes "search
        // query text" - the audit table (grant-protected, append-only) is where who-
        // searched-what lives; §15 keeps the same text out of telemetry
        // (TelemetryHygieneTests proves that side with a sentinel through this path).
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "audited", "Audited", "ullage-pressure procedures");

        var uniqueQuery = $"ullage-pressure-{Guid.NewGuid():N}";
        var client = CreateUserClient();
        using var _ = await client.PostGraphQLAsync($$"""
            query { search(query: "{{uniqueQuery}}", spaceKey: "{{space.Key}}") { totalCount } }
            """);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var row = Assert.Single(db.AuditEvents.Where(e => e.Action == "search.query" && e.DetailsJson!.Contains(uniqueQuery)));

        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Equal(AuditChannel.GraphQl, row.Channel);
        Assert.Equal(space.Key, row.SpaceKey);
        Assert.NotNull(row.UserId);

        using var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal(uniqueQuery, details.RootElement.GetProperty("query").GetString());
        Assert.Equal(space.Key, details.RootElement.GetProperty("spaceKey").GetString());
        Assert.Equal(0, details.RootElement.GetProperty("resultCount").GetInt32());
    }

    [Fact]
    public async Task Search_LabelsArgument_FiltersToLabeledPages()
    {
        var (space, _) = await SeedPagesWithLabelAsync();

        var client = CreateUserClient();
        using var response = await client.PostGraphQLAsync($$"""
            query { search(query: "labeled-keyword", labels: ["howto-{{space.Key}}"]) { edges { node { page { title } } } } }
            """);

        var edge = Assert.Single(response.RootElement.GetProperty("data").GetProperty("search")
            .GetProperty("edges").EnumerateArray());
        Assert.Equal("Labeled", edge.GetProperty("node").GetProperty("page").GetProperty("title").GetString());
    }

    [Fact]
    public async Task LabelsQuery_ListsLabelNamesFromViewableSpacesOnly()
    {
        var (space, _) = await SeedPagesWithLabelAsync();

        // Same facets operation shape the SPA ships (SearchFacets).
        var client = CreateUserClient();
        using var response = await client.PostGraphQLAsync($$"""
            query { spaces { key name } labels(spaceKey: "{{space.Key}}") }
            """);

        var labels = response.RootElement.GetProperty("data").GetProperty("labels")
            .EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Equal([$"howto-{space.Key}"], labels);

        // A space granted to a group the caller isn't in: its labels are absent from
        // the unfiltered list, and naming its key yields an empty list
        // indistinguishable from "no labels" (design.md §6.7).
        var (hiddenSpace, hiddenCreator) = await SeedSpaceAsync(
            RuleExpressionSerializer.Serialize(new GroupCondition("insiders-only")));
        await SeedLabelAsync(hiddenSpace, hiddenCreator, $"secret-label-{hiddenSpace.Key}");

        using var allLabels = await client.PostGraphQLAsync("query { labels }");
        Assert.DoesNotContain($"secret-label-{hiddenSpace.Key}",
            allLabels.RootElement.GetProperty("data").GetProperty("labels")
                .EnumerateArray().Select(l => l.GetString()));

        using var hiddenByKey = await client.PostGraphQLAsync($$"""
            query { labels(spaceKey: "{{hiddenSpace.Key}}") }
            """);
        Assert.Empty(hiddenByKey.RootElement.GetProperty("data").GetProperty("labels").EnumerateArray());
    }

    private async Task<(Space Space, User Creator)> SeedPagesWithLabelAsync()
    {
        var (space, creator) = await SeedSpaceAsync();
        var labeled = await SeedPageAsync(space, "labeled", "Labeled", "labeled-keyword content");
        await SeedPageAsync(space, "unlabeled", "Unlabeled", "labeled-keyword content");
        await SeedLabelAsync(space, creator, $"howto-{space.Key}", labeled.Id);
        return (space, creator);
    }

    private async Task SeedLabelAsync(Space space, User creator, string name, Guid? pageId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var label = new Label { SpaceId = space.Id, Name = name };
        db.Labels.Add(label);
        if (pageId is not null)
        {
            db.PageLabels.Add(new PageLabel { PageId = pageId.Value, LabelId = label.Id });
        }

        await db.SaveChangesAsync();
    }
}
