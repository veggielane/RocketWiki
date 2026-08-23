using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Search;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Configurations;
using RocketWiki.Data.Services;
using RocketWiki.Data.Tests;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// design.md §9.3 on the real engine: the native vector(1536) column (the
/// AlterPageEmbeddingToNativeVector migration), the SqlVector&lt;float&gt; write path
/// (RocketWikiDbContext's SQL Server Embedding mapping), and SearchService's
/// VECTOR_DISTANCE('cosine', …) branch — the three pieces the SQLite tier structurally
/// cannot execute. Mirrors the high-value scenarios of the SQLite tier's
/// HybridSearchServiceTests (same faked embedding endpoint, same real
/// EmbeddingIndexer + SearchService), so the public behavior is byte-identical across
/// providers by construction: a semantic-only hit that no keyword branch could
/// produce, and the §6.7 adversarial re-run proving restricted pages stay absent on
/// this path too. Vectors here are 1536-dimensional (the column fixes that — see
/// PageEmbeddingConfiguration.EmbeddingDimensions), with the test's semantic axes in
/// the first components.
/// </summary>
public sealed class VectorSearchTests : SqlServerTestBase
{
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-vector", "127.0.0.1");

    public VectorSearchTests(SqlServerContainerFixture fixture)
        : base(fixture)
    {
    }

    private static EmbeddingOptions Options(int? dimensions = null) =>
        new("fake-model", dimensions ?? PageEmbeddingConfiguration.EmbeddingDimensions, FailureBackoff: TimeSpan.Zero);

    /// <summary>
    /// Concept axes in a full-width vector: axis 0 = thermal-protection concept (query
    /// "thermal", content about "heat"), axis 1 = everything else. No literal substring
    /// and no FTS stem links "thermal" to "heat" — a hit can only come from the vector
    /// branch, which on this tier is VECTOR_DISTANCE over the native column.
    /// </summary>
    private static float[] ThermalConceptVector(string text)
    {
        var thermal = text.Contains("thermal", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("heat", StringComparison.OrdinalIgnoreCase);
        var vector = new float[PageEmbeddingConfiguration.EmbeddingDimensions];
        vector[0] = thermal ? 1f : 0f;
        vector[1] = thermal ? 0f : 1f;
        vector[2] = 0.01f;
        return vector;
    }

    private static Principal Viewer(params string[] groups) => Principal.Create("viewer-sub", groups);

    private static AccessRule ViewerGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
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

    [SqlServerFact]
    public void Embedding_RoundTrips_ExactFloats_ThroughTheNativeVectorColumn()
    {
        // The SQL Server twin of the SQLite tier's PageEmbeddingConversionTests: the
        // float[] ⇄ SqlVector<float> conversion against a real vector(1536) column.
        // float32 survives the trip bit-exactly — vector elements ARE float32.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var vector = Enumerable.Range(0, PageEmbeddingConfiguration.EmbeddingDimensions)
            .Select(i => (float)Math.Sin(i * 0.01)).ToArray();

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageEmbeddings.Add(new PageEmbedding
            {
                PageId = page.Id,
                ChunkIndex = 0,
                HeadingPath = "Introduction",
                ChunkHash = new byte[32],
                Embedding = vector,
                Model = "test-embedding-model",
                UpdatedAtUtc = DateTime.UtcNow,
            });
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloaded = readContext.PageEmbeddings.Single(e => e.PageId == page.Id);
        Assert.Equal(vector, reloaded.Embedding);
    }

    /// <summary>Filler long enough (about 2200 chars) that each section stays its own
    /// chunk under default chunker options — same fixture as the SQLite tier.</summary>
    private static string Filler(string word) =>
        string.Join(" ", Enumerable.Repeat($"{word} lorem ipsum dolor sit amet", 80));

    [SqlServerFact]
    public async Task Search_SemanticOnlyMatch_IsFound_ViaVectorDistance_WithSectionDeepLink()
    {
        // Mirrors the SQLite tier's SemanticOnlyMatch_NoKeywordOverlap_IsFound: the
        // query "thermal" appears NOWHERE in the content and shares no FTS stem with
        // "heat", so CONTAINSTABLE and LIKE both come up empty — the hit, and its
        // chunk-level deep link (best chunk per page via ROW_NUMBER over
        // VECTOR_DISTANCE, section recomputed only after canView), can only come from
        // the native vector branch.
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, DefaultLocalInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "shielding", "Orbiter protection",
                $"# Protection\n\n{Filler("structural")}\n\n## Heat tiles\n\n{Filler("heat resistant tiles")}\n"),
            Viewer(), user.Id, AuditCtx);
        Assert.True(page.IsSuccess);

        var generator = new FakeEmbeddingGenerator(ThermalConceptVector);
        var indexer = new EmbeddingIndexer(context, generator, Options(), NullLogger<EmbeddingIndexer>.Instance);
        var run = await indexer.RunOnceAsync();
        Assert.Equal(0, run.PagesFailed); // fixture sanity: the page embedded into the vector column

        var searchService = new SearchService(context, generator, Options(), NullLogger<SearchService>.Instance);
        var hits = await searchService.SearchAsync(
            new SearchRequest("thermal", null, null), Viewer(), maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(page.Value.Id, hit.PageId);
        // Deep-link attribution to the best-matching CHUNK — the ChunkIndex travelled
        // from the SQL ROW_NUMBER, the section text was recomputed post-canView.
        Assert.Equal(["Protection", "Heat tiles"], hit.HeadingPath);
        Assert.Equal("protection--heat-tiles", hit.AnchorId);
        Assert.Contains("heat resistant tiles", hit.Snippet, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Search_OnTheVectorDistanceBranch_RestrictedPage_IsAbsentNotForbidden()
    {
        // §6.7 adversarial re-run on THIS candidate path (mirrors the SQLite tier's
        // RestrictedPage_WithEmbeddingsInTheStore_IsAbsentNotForbidden and the FTS
        // tier's equivalent): the restricted page is deliberately the STRONGEST
        // semantic match, so if the over-fetch → canView → recompute-after-canView
        // pipeline leaked anywhere on the VECTOR_DISTANCE branch, this is where it
        // would show.
        const string secretPhrase = "the-classified-heat-shield-recipe";

        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, DefaultLocalInstanceId);

        var visible = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "tps-overview", "Tile overview",
                "# Overview\n\nThe heat shield tiles are inspected quarterly."),
            Viewer(), user.Id, AuditCtx);
        Assert.True(visible.IsSuccess);

        var restricted = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "tps-secret", $"Heat {secretPhrase}",
                $"# Secret\n\nheat heat heat {secretPhrase} heat heat."),
            Viewer(), user.Id, AuditCtx);
        Assert.True(restricted.IsSuccess);

        context.AccessRules.Add(GroupViewRestriction(restricted.Value.Id, "top-secret"));
        context.SaveChanges();

        var generator = new FakeEmbeddingGenerator(ThermalConceptVector);
        var indexer = new EmbeddingIndexer(context, generator, Options(), NullLogger<EmbeddingIndexer>.Instance);
        var run = await indexer.RunOnceAsync();
        Assert.Equal(0, run.PagesFailed);

        // Both pages verifiably HAVE vectors in the store before searching — the
        // restricted page's absence below can only be the permission filter.
        Assert.Equal(2, await context.PageEmbeddings.Select(e => e.PageId).Distinct().CountAsync());

        var searchService = new SearchService(context, generator, Options(), NullLogger<SearchService>.Instance);
        var hits = await searchService.SearchAsync(
            new SearchRequest("thermal", null, null), Viewer(), maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(visible.Value.Id, hit.PageId);

        // §6.7: absent means absent — no returned field may carry the restricted text.
        var everyReturnedString = string.Join('\n',
            hits.SelectMany(h => new[] { h.Title, h.Snippet, h.AnchorId }.Concat(h.HeadingPath)));
        Assert.DoesNotContain(secretPhrase, everyReturnedString, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task Indexer_VectorsOfWrongDimensions_AreRejectedByTheColumn_NeverStored()
    {
        // The schema-level backstop the varbinary blob never had: vector(1536) rejects
        // an 8-dimensional write outright. The indexer's §9.2 failure isolation turns
        // that into a recorded per-page failure (retried after backoff) rather than a
        // crash — but nothing may be stored, and the failure must be visible in the run.
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        var wrongDims = new FakeEmbeddingGenerator(_ => new float[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0.5f });
        var indexer = new EmbeddingIndexer(
            context, wrongDims, Options(dimensions: 8), NullLogger<EmbeddingIndexer>.Instance);

        var run = await indexer.RunOnceAsync();

        Assert.Equal(1, run.PagesFailed);
        Assert.True(run.Aborted);
        Assert.Equal(0, await context.PageEmbeddings.CountAsync());
        var state = await context.PageEmbeddingStates.SingleAsync(s => s.PageId == page.Id);
        Assert.True(state.FailedAttempts > 0);
    }

    [SqlServerFact]
    public async Task Search_QueryVectorOfWrongDimensions_FailsLoudly_NotKeywordOnly()
    {
        // Store dimension mismatches are configuration errors, not endpoint weather:
        // §9.2's graceful keyword-only degrade covers a FAILING embedding endpoint
        // only. A generator that answers with vectors the column can't compare must
        // surface as an error — silently returning keyword-only results would hide a
        // misconfiguration indefinitely.
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, DefaultLocalInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "tps", "Tile bonding",
                "# Tile bonding\n\nThe heat shield tiles bond to the airframe."),
            Viewer(), user.Id, AuditCtx);
        Assert.True(page.IsSuccess);

        // Seed the store correctly (1536-dim vectors)…
        var goodGenerator = new FakeEmbeddingGenerator(ThermalConceptVector);
        var indexer = new EmbeddingIndexer(context, goodGenerator, Options(), NullLogger<EmbeddingIndexer>.Instance);
        var run = await indexer.RunOnceAsync();
        Assert.Equal(0, run.PagesFailed);

        // …then search with a misconfigured 8-dimensional query pipeline.
        var wrongDims = new FakeEmbeddingGenerator(_ => new float[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0.5f });
        var searchService = new SearchService(
            context, wrongDims, Options(dimensions: 8), NullLogger<SearchService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            searchService.SearchAsync(new SearchRequest("thermal", null, null), Viewer(), maxResults: 10));
    }
}
