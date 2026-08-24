using Microsoft.Extensions.Logging.Abstractions;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Search;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §9.2/§9.3 (milestone 7): hybrid retrieval through the REAL pipeline —
/// pages are embedded by the real <see cref="EmbeddingIndexer"/> (chunker, hashes,
/// store) and searched by the real <see cref="SearchService"/> (LIKE keyword +
/// exact-scan cosine + RRF), with only the embedding endpoint faked. Semantic
/// similarity in these tests is spelled out per test as a tiny concept-axis vector
/// function, so "these two texts are similar" is a visible fixture, not an emergent
/// property of a fake.
/// </summary>
public class HybridSearchServiceTests : SqliteTestBase
{
    private const int Dimensions = 4;

    private static EmbeddingOptions Options() => new("fake-model", Dimensions, FailureBackoff: TimeSpan.Zero);

    /// <summary>
    /// Concept axes: axis 0 = thermal-protection concept (the query "thermal" and
    /// content about "heat"), axis 1 = everything else. No literal substring links
    /// "thermal" to "heat" — that's the point: only the vector store can make this hit.
    /// </summary>
    private static float[] ThermalConceptVector(string text)
    {
        var thermal = text.Contains("thermal", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("heat", StringComparison.OrdinalIgnoreCase);
        return [thermal ? 1f : 0f, thermal ? 0f : 1f, 0.01f, 0f];
    }

    /// <summary>Filler long enough (about 2200 chars) that each section stays its own chunk under default chunker options.</summary>
    private static string Filler(string word) =>
        string.Join(" ", Enumerable.Repeat($"{word} lorem ipsum dolor sit amet", 80));

    private static Principal Viewer(params string[] groups) => Principal.Create("viewer-sub", groups);

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

    private static AccessRule GroupViewRestriction(Guid pageId, string group) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = $$"""{ "group": "{{group}}" }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private async Task EmbedAllAsync(FakeEmbeddingGenerator generator)
    {
        using var db = CreateContext();
        var indexer = new EmbeddingIndexer(db, generator, Options(), NullLogger<EmbeddingIndexer>.Instance);
        var run = await indexer.RunOnceAsync();
        Assert.Equal(0, run.PagesFailed); // fixture sanity: everything seeded must actually embed
    }

    private SearchService NewSearchService(RocketWikiDbContext db, FakeEmbeddingGenerator generator) =>
        new(db, NullLogger<SearchService>.Instance, generator, Options());

    [Fact]
    public async Task SemanticOnlyMatch_NoKeywordOverlap_IsFound_WithSectionDeepLink()
    {
        var generator = new FakeEmbeddingGenerator(ThermalConceptVector);
        Guid pageId;
        using (var db = CreateContext())
        {
            var space = TestData.NewSpace();
            var page = TestData.NewPage(space, "shielding");
            page.Title = "Orbiter protection";
            // Section 1 is unrelated; section 2 is about heat tiles. The query "thermal"
            // appears NOWHERE in this content - LIKE cannot find it.
            page.CurrentContent = $"# Protection\n\n{Filler("structural")}\n\n## Heat tiles\n\n{Filler("heat resistant tiles")}\n";
            db.Spaces.Add(space);
            db.Pages.Add(page);
            db.AccessRules.Add(ViewerGrant(space.Id));
            db.SaveChanges();
            pageId = page.Id;
        }

        await EmbedAllAsync(generator);

        using var searchDb = CreateContext();
        var hits = await NewSearchService(searchDb, generator)
            .SearchAsync(new SearchRequest("thermal", null, null), Viewer(), maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(pageId, hit.PageId);
        // Deep-link attribution to the best-matching CHUNK, through the same anchor
        // contract keyword hits use (§9.2's whole point for chunk-level embedding).
        Assert.Equal(["Protection", "Heat tiles"], hit.HeadingPath);
        Assert.Equal("protection--heat-tiles", hit.AnchorId);
        Assert.Contains("heat resistant tiles", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rrf_PageMatchingBothSignals_OutranksSingleSignalPages_AndAllThreeSurface()
    {
        // Axis 0 = the "raptor bird" concept: the query "falcon" and content containing
        // "eagle". P_both matches keyword (literal "falcon") AND concept ("eagle");
        // P_kw matches keyword only; P_vec concept only. P_vec's "habitat" adds a small
        // off-axis component so its similarity is strictly below P_both's - similarities
        // must be strictly ordered or the vector ranking would tie-break on page id and
        // the assertion would flap.
        static float[] BirdVector(string text)
        {
            if (text == "falcon")
            {
                return [1f, 0f, 0.01f, 0f]; // the query itself
            }

            var bird = text.Contains("eagle", StringComparison.OrdinalIgnoreCase);
            var offAxis = text.Contains("habitat", StringComparison.OrdinalIgnoreCase) ? 0.3f : bird ? 0f : 1f;
            return [bird ? 1f : 0f, offAxis, 0.01f, 0f];
        }

        var generator = new FakeEmbeddingGenerator(BirdVector);
        Guid bothId, keywordOnlyId, vectorOnlyId;
        using (var db = CreateContext())
        {
            var space = TestData.NewSpace();
            var both = TestData.NewPage(space, "both");
            both.CurrentContent = $"# Raptors\n\nfalcon and eagle notes {Filler("wingspan")}";
            // Keyword-only page is NEWEST, so the LIKE list ranks it first - proving the
            // fused order overcomes single-list rank, not just inheriting it.
            var keywordOnly = TestData.NewPage(space, "kw");
            keywordOnly.CurrentContent = $"# Migration\n\nfalcon sightings {Filler("route")}";
            keywordOnly.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(5);
            var vectorOnly = TestData.NewPage(space, "vec");
            vectorOnly.CurrentContent = $"# Nesting\n\neagle habitat {Filler("cliff")}";

            db.Spaces.Add(space);
            db.Pages.AddRange(both, keywordOnly, vectorOnly);
            db.AccessRules.Add(ViewerGrant(space.Id));
            db.SaveChanges();
            (bothId, keywordOnlyId, vectorOnlyId) = (both.Id, keywordOnly.Id, vectorOnly.Id);
        }

        await EmbedAllAsync(generator);

        using var searchDb = CreateContext();
        var hits = await NewSearchService(searchDb, generator)
            .SearchAsync(new SearchRequest("falcon", null, null), Viewer(), maxResults: 10);

        Assert.Equal(3, hits.Count);
        // §9.3 RRF: both-signals beats either single signal…
        Assert.Equal(bothId, hits[0].PageId);
        // …and the semantic-only page (zero keyword overlap with "falcon") still surfaces.
        Assert.Contains(hits, h => h.PageId == vectorOnlyId);
        Assert.Contains(hits, h => h.PageId == keywordOnlyId);
    }

    [Fact]
    public async Task RestrictedPage_WithEmbeddingsInTheStore_IsAbsentNotForbidden()
    {
        var generator = new FakeEmbeddingGenerator(ThermalConceptVector);
        Guid restrictedId, visibleId;
        using (var db = CreateContext())
        {
            var space = TestData.NewSpace();
            var restricted = TestData.NewPage(space, "classified");
            restricted.Title = "Classified thermal program";
            restricted.CurrentContent = $"# Secret\n\nheat shielding for the classified vehicle {Filler("heat")}";
            var visible = TestData.NewPage(space, "public");
            visible.CurrentContent = $"# Public\n\nheat tiles overview {Filler("heat")}";

            db.Spaces.Add(space);
            db.Pages.AddRange(restricted, visible);
            db.AccessRules.Add(ViewerGrant(space.Id));
            db.AccessRules.Add(GroupViewRestriction(restricted.Id, "thermal-program"));
            db.SaveChanges();
            (restrictedId, visibleId) = (restricted.Id, visible.Id);
        }

        // The store DOES hold the restricted page's chunks - embedding is
        // permission-blind by design (§9.4: each instance embeds all its content).
        await EmbedAllAsync(generator);
        using (var db = CreateContext())
        {
            Assert.True(db.PageEmbeddings.Any(e => e.PageId == restrictedId));
        }

        // §6.7: the caller without the group sees the visible hit ONLY - the restricted
        // page is absent (not an error, not a stub, not a count), even though its
        // vectors scored at least as well in the scan.
        using var searchDb = CreateContext();
        var hits = await NewSearchService(searchDb, generator)
            .SearchAsync(new SearchRequest("thermal", null, null), Viewer(), maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(visibleId, hit.PageId);

        // And with the group, the same search finds both.
        var privilegedHits = await NewSearchService(searchDb, generator)
            .SearchAsync(new SearchRequest("thermal", null, null), Viewer("thermal-program"), maxResults: 10);
        Assert.Equal(2, privilegedHits.Count);
        Assert.Contains(privilegedHits, h => h.PageId == restrictedId);
    }

    [Fact]
    public async Task QueryEmbeddingFails_SearchDegradesToKeywordOnly()
    {
        var generator = new FakeEmbeddingGenerator(ThermalConceptVector);
        Guid keywordPageId;
        using (var db = CreateContext())
        {
            var space = TestData.NewSpace();
            var page = TestData.NewPage(space, "kw");
            page.CurrentContent = "# Notes\n\nthermal blanket inspection";
            db.Spaces.Add(space);
            db.Pages.Add(page);
            db.AccessRules.Add(ViewerGrant(space.Id));
            db.SaveChanges();
            keywordPageId = page.Id;
        }

        await EmbedAllAsync(generator);

        // Endpoint goes down between indexing and this query: §9.2 - degrade to
        // keyword-only, never error the search.
        generator.ThrowOnGenerate = new HttpRequestException("endpoint down");

        using var searchDb = CreateContext();
        var hits = await NewSearchService(searchDb, generator)
            .SearchAsync(new SearchRequest("thermal", null, null), Viewer(), maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(keywordPageId, hit.PageId);
    }

    [Fact]
    public async Task VectorsFromAnotherModel_AreIgnoredAtQueryTime()
    {
        var generator = new FakeEmbeddingGenerator(ThermalConceptVector);
        using (var db = CreateContext())
        {
            var space = TestData.NewSpace();
            var page = TestData.NewPage(space, "old-model");
            page.CurrentContent = $"# Archive\n\nheat data {Filler("heat")}";
            db.Spaces.Add(space);
            db.Pages.Add(page);
            db.AccessRules.Add(ViewerGrant(space.Id));
            db.SaveChanges();
        }

        await EmbedAllAsync(generator);

        // Simulate a model change without re-embed: rows stamped with a different model.
        using (var db = CreateContext())
        {
            foreach (var row in db.PageEmbeddings.ToList())
            {
                row.Model = "some-older-model";
            }

            db.SaveChanges();
        }

        // §9.3 "an index only ever contains one model's vectors": mismatched rows are
        // not comparable, so the semantic-only query finds nothing rather than garbage.
        using var searchDb = CreateContext();
        var hits = await NewSearchService(searchDb, generator)
            .SearchAsync(new SearchRequest("thermal", null, null), Viewer(), maxResults: 10);

        Assert.Empty(hits);
    }
}
