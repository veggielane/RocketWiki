using Microsoft.Extensions.Logging.Abstractions;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §9.1/§9.3: covers the LIKE fallback path, which is what the SQLite tier
/// actually exercises (SearchService's SQL Server CONTAINSTABLE path is unverified
/// here - see the TODO comment on SearchService.SearchViaFullTextAsync and the
/// migration's FTS raw SQL). Facets and the over-fetch-then-filter permission behavior
/// are provider-agnostic, so they're fully covered.
/// </summary>
public class SearchServiceTests : SqliteTestBase
{
    private static Principal ViewerPrincipal(params string[] groups) => Principal.Create("viewer-sub", groups);

    private static AccessRule ViewerGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Viewer,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule ViewRestriction(Guid pageId, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = expressionJson,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Search_MatchesTitleOrContent_ReturnsHit()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "rocket-engines");
        page.Title = "Rocket Engine Design";
        page.CurrentContent = "# Combustion chambers and nozzles";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("combustion", null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Single(result);
        Assert.Equal(page.Id, result[0].PageId);
        Assert.Equal(space.Key, result[0].SpaceKey);
    }

    /// <summary>
    /// The LIKE fallback interpolates the raw query into a pattern, so its
    /// metacharacters used to be live: <c>100%</c> matched anything containing "100",
    /// <c>a_b</c> matched "axb", and a bare <c>%</c> matched the whole table. A search
    /// returning far MORE than asked for is the quiet kind of wrong — nobody reports it —
    /// and this is the branch the entire §14 integration tier runs on, so the tested
    /// behaviour differed from production for any query with punctuation in it.
    /// </summary>
    [Theory]
    [InlineData("100%")]
    [InlineData("a_b")]
    [InlineData("[abc]")]
    public async Task Search_QueryContainingLikeMetacharacters_MatchesThemLiterally(string query)
    {
        var space = TestData.NewSpace();
        var literal = TestData.NewPage(space, "literal");
        literal.Title = "Thrust margin";
        literal.CurrentContent = $"# Margin\n\nMeasured at {query} of nominal.";

        // The page a live wildcard would have dragged in: it shares no literal substring
        // with any of the queries above, so it can only match if the pattern is wild.
        var decoy = TestData.NewPage(space, "decoy");
        decoy.Title = "Unrelated";
        decoy.CurrentContent = "# Unrelated\n\nNothing in common.";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(literal, decoy);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest(query, null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Equal(literal.Id, Assert.Single(result).PageId);
    }

    /// <summary>A query that is nothing BUT wildcards used to match every page in the
    /// instance; escaped, it matches only a page that literally contains it.</summary>
    [Fact]
    public async Task Search_QueryOfBareWildcards_DoesNotMatchEverything()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "ordinary");
        page.Title = "Ordinary page";
        page.CurrentContent = "# Ordinary\n\nNo wildcards here.";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("%", null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentContent = "Nothing relevant here.";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("zzz-nonexistent-term", null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty_NoWastedQuery()
    {
        using var context = CreateContext();
        var service = new SearchService(context, NullLogger<SearchService>.Instance);

        var result = await service.SearchAsync(new SearchRequest("   ", null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Search_SpaceFacet_ExcludesMatchesFromOtherSpaces()
    {
        var spaceA = TestData.NewSpace("ENG");
        var spaceB = TestData.NewSpace("OPS");
        var pageA = TestData.NewPage(spaceA, "a");
        var pageB = TestData.NewPage(spaceB, "b");
        pageA.CurrentContent = "shared-keyword content A";
        pageB.CurrentContent = "shared-keyword content B";

        using var context = CreateContext();
        context.Spaces.AddRange(spaceA, spaceB);
        context.Pages.AddRange(pageA, pageB);
        context.AccessRules.Add(ViewerGrant(spaceA.Id));
        context.AccessRules.Add(ViewerGrant(spaceB.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("shared-keyword", "ENG", null), ViewerPrincipal(), maxResults: 10);

        Assert.Single(result);
        Assert.Equal(pageA.Id, result[0].PageId);
    }

    [Fact]
    public async Task Search_LabelFacet_MatchesAnyOfTheGivenLabels()
    {
        var space = TestData.NewSpace();
        var pageWithLabelA = TestData.NewPage(space, "a");
        var pageWithLabelB = TestData.NewPage(space, "b");
        var pageWithNoLabel = TestData.NewPage(space, "c");
        pageWithLabelA.CurrentContent = "keyword content";
        pageWithLabelB.CurrentContent = "keyword content";
        pageWithNoLabel.CurrentContent = "keyword content";
        var labelA = new Label { SpaceId = space.Id, Name = "how-to" };
        var labelB = new Label { SpaceId = space.Id, Name = "reference" };

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(pageWithLabelA, pageWithLabelB, pageWithNoLabel);
        context.Labels.AddRange(labelA, labelB);
        context.PageLabels.Add(new PageLabel { PageId = pageWithLabelA.Id, LabelId = labelA.Id });
        context.PageLabels.Add(new PageLabel { PageId = pageWithLabelB.Id, LabelId = labelB.Id });
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(
            new SearchRequest("keyword", null, new[] { "how-to", "reference" }), ViewerPrincipal(), maxResults: 10);

        var resultIds = result.Select(h => h.PageId).ToList();
        Assert.Contains(pageWithLabelA.Id, resultIds);
        Assert.Contains(pageWithLabelB.Id, resultIds);
        Assert.DoesNotContain(pageWithNoLabel.Id, resultIds);
    }

    [Fact]
    public async Task Search_RestrictedMatch_IsAbsentFromResults_NotJustFilteredWithAGap()
    {
        // design.md §6.7/§9.3: absent, not forbidden - same rule as every other read.
        var space = TestData.NewSpace();
        var visiblePage = TestData.NewPage(space, "visible");
        var restrictedPage = TestData.NewPage(space, "restricted");
        visiblePage.CurrentContent = "keyword content one";
        restrictedPage.CurrentContent = "keyword content two";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(visiblePage, restrictedPage);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(restrictedPage.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("keyword", null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Single(result);
        Assert.Equal(visiblePage.Id, result[0].PageId);
    }

    [Fact]
    public async Task Search_OverFetchesBeforeFiltering_SoARestrictionHeavyResultSetIsNotEmpty()
    {
        // design.md §9.3: over-fetch top-K so that when maxResults=1 and the single
        // best-looking candidate happens to be restricted, a visible result further
        // down the (recency-ordered, for LIKE) list still comes back - the search
        // doesn't stop at the first K before permission filtering even applies.
        var space = TestData.NewSpace();
        var now = DateTime.UtcNow;

        var restrictedNewest = TestData.NewPage(space, "restricted");
        restrictedNewest.CurrentContent = "keyword content";
        restrictedNewest.UpdatedAtUtc = now; // most recent - LIKE fallback orders by UpdatedAtUtc desc

        var visibleOlder = TestData.NewPage(space, "visible");
        visibleOlder.CurrentContent = "keyword content";
        visibleOlder.UpdatedAtUtc = now.AddMinutes(-5);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(restrictedNewest, visibleOlder);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(restrictedNewest.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        // Ask for just 1 result - without over-fetching, a naive "take 1 then filter"
        // implementation would filter the single restricted candidate down to nothing.
        var result = await service.SearchAsync(new SearchRequest("keyword", null, null), ViewerPrincipal(), maxResults: 1);

        Assert.Single(result);
        Assert.Equal(visibleOlder.Id, result[0].PageId);
    }

    [Fact]
    public async Task Search_RespectsMaxResults()
    {
        var space = TestData.NewSpace();
        var pages = Enumerable.Range(0, 5).Select(i =>
        {
            var page = TestData.NewPage(space, $"page-{i}");
            page.CurrentContent = "keyword content";
            return page;
        }).ToList();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(pages);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("keyword", null, null), ViewerPrincipal(), maxResults: 2);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Search_HitInsideASection_CarriesHeadingPathAndAnchorId()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "engines");
        page.CurrentContent = "# Engines\n\nintro text\n\n## Turbopumps\n\nThe impeller-cavitation margin is thin.\n\n## Nozzles\n\nunrelated";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("impeller-cavitation", null, null), ViewerPrincipal(), maxResults: 10);

        var hit = Assert.Single(result);
        Assert.Equal(new[] { "Engines", "Turbopumps" }, hit.HeadingPath);
        Assert.Equal("engines--turbopumps", hit.AnchorId);
        Assert.Contains("impeller-cavitation", hit.Snippet);
    }

    [Fact]
    public async Task Search_TitleOnlyMatch_EmptyHeadingPath_LeadingSnippet()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "unique");
        page.Title = "Cryogenic-Loading Procedures";
        page.CurrentContent = "# Overview\n\nNothing containing the search word.";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("Cryogenic-Loading", null, null), ViewerPrincipal(), maxResults: 10);

        var hit = Assert.Single(result);
        Assert.Empty(hit.HeadingPath);
        Assert.Equal(string.Empty, hit.AnchorId);
        Assert.StartsWith("# Overview", hit.Snippet);
    }

    [Fact]
    public async Task Search_MatchDeepInLongPage_SnippetIsCenteredOnTheMatch_NotAPrefix()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "long");
        page.CurrentContent = "# Long\n\n" + string.Join(' ', Enumerable.Repeat("filler", 300))
            + " hydrazine-loading " + string.Join(' ', Enumerable.Repeat("filler", 300));

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("hydrazine-loading", null, null), ViewerPrincipal(), maxResults: 10);

        var hit = Assert.Single(result);
        Assert.Contains("hydrazine-loading", hit.Snippet);
        Assert.StartsWith("…", hit.Snippet); // a naive prefix excerpt would never contain a match this deep
    }

    [Fact]
    public async Task Search_RestrictedPagesContent_NeverSurfacesInAnyReturnedField()
    {
        // The adversarial version of "absent, not forbidden" (design.md §6.7): not
        // only is the restricted page's hit missing, no OTHER hit's snippet, title,
        // path, or anchor carries its text either - the restricted content must
        // never have entered the excerpting code at all.
        const string secretPhrase = "the-classified-payload-manifest";
        var space = TestData.NewSpace();
        var visible = TestData.NewPage(space, "visible");
        visible.CurrentContent = "shared-term in public content";
        var restricted = TestData.NewPage(space, "restricted");
        restricted.Title = $"Secret {secretPhrase}";
        restricted.CurrentContent = $"shared-term next to {secretPhrase}";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(visible, restricted);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(restricted.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("shared-term", null, null), ViewerPrincipal(), maxResults: 10);

        var hit = Assert.Single(result);
        Assert.Equal(visible.Id, hit.PageId);

        var everyReturnedString = string.Join('\n',
            result.SelectMany(h => new[] { h.Title, h.Snippet, h.AnchorId }.Concat(h.HeadingPath)));
        Assert.DoesNotContain(secretPhrase, everyReturnedString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_NoSpaceRoleAtAll_ReturnsEmpty()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentContent = "keyword content";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        // No AccessRule grant at all for this space.
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var result = await service.SearchAsync(new SearchRequest("keyword", null, null), ViewerPrincipal(), maxResults: 10);

        Assert.Empty(result);
    }
}
