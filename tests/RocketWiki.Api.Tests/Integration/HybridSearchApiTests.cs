using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Milestone 7 at the API boundary (design.md §6.7/§8/§9): hybrid search behind the
/// UNCHANGED `search` field — the shipped SPA operation (search.graphql) posts the same
/// document it always has; §9's "two retrieval modes fused into one search experience"
/// means hybrid is a service-layer upgrade, not new schema. Adversarial coverage: a
/// restricted page whose chunks ARE in the vector store must be absent (never
/// forbidden), and an unreachable embedding endpoint must degrade search while page
/// saves proceed untouched.
/// </summary>
public sealed class HybridSearchApiTests(EmbeddingApiFixture fixture) : IClassFixture<EmbeddingApiFixture>
{
    /// <summary>Long enough (about 2200 chars, past the chunker's TargetChars) that each section stays its own chunk under default chunker options.</summary>
    private static string Filler(string word) =>
        string.Join(" ", Enumerable.Repeat($"{word} lorem ipsum dolor sit amet", 80));

    private static float[] ThermalConceptVector(string text)
    {
        var thermal = text.Contains("thermal", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("heat", StringComparison.OrdinalIgnoreCase);
        return [thermal ? 1f : 0f, thermal ? 0f : 1f, 0.01f, 0f];
    }

    private async Task<(Space Space, User Creator)> SeedSpaceAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
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
            Key = $"H{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Hybrid Search Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        }));

        await db.SaveChangesAsync();
        return (space, creator);
    }

    private async Task<Page> SeedPageAsync(Space space, string slug, string title, string content, string? restrictToGroup = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = slug,
            Title = title,
            CurrentContent = content,
            CurrentRevisionNumber = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
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
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"hybrid-{Guid.NewGuid():N}", email: "h@example.test", name: "Hybrid Searcher", groups: groups);
        return client;
    }

    /// <summary>Field-for-field the SPA's SearchPages operation (web/src/graphql/operations/search.graphql) with variables inlined.</summary>
    private static string SpaSearchDocument(string query, string spaceKey) => $$"""
        query SearchPages {
          search(query: "{{query}}", spaceKey: "{{spaceKey}}") {
            totalCount
            pageInfo { hasNextPage endCursor }
            edges { cursor node { snippet headingPath anchorId page { id title spaceKey } } }
          }
        }
        """;

    [Fact]
    public async Task SemanticOnlyHit_ThroughTheUnchangedSpaOperation_CarriesSectionDeepLink()
    {
        fixture.Generator.Embed = ThermalConceptVector;
        var (space, _) = await SeedSpaceAsync();
        // "thermal" appears nowhere in this content - only the vector store links them.
        var page = await SeedPageAsync(space, "tiles", "Orbiter protection",
            $"# Protection\n\n{Filler("structural frame")}\n\n## Heat tiles\n\n{Filler("heat resistant tiles")}\n");

        var run = await fixture.RunIndexerAsync();
        Assert.Equal(0, run.PagesFailed);

        var client = CreateUserClient();
        using var response = await client.PostGraphQLAsync(SpaSearchDocument("thermal", space.Key));

        var search = response.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(1, search.GetProperty("totalCount").GetInt32());

        var node = search.GetProperty("edges")[0].GetProperty("node");
        Assert.Equal(
            new[] { "Protection", "Heat tiles" },
            node.GetProperty("headingPath").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("protection--heat-tiles", node.GetProperty("anchorId").GetString());
        Assert.Contains("heat resistant tiles", node.GetProperty("snippet").GetString(), StringComparison.Ordinal);

        // The page field still resolves through the object-level canView path.
        var pageNode = node.GetProperty("page");
        Assert.Equal(page.Id.ToString(), pageNode.GetProperty("id").GetString());
        Assert.Equal("Orbiter protection", pageNode.GetProperty("title").GetString());
        Assert.Equal(space.Key, pageNode.GetProperty("spaceKey").GetString());
    }

    [Fact]
    public async Task RestrictedPage_WithChunksInTheVectorStore_IsAbsentFromTheResponseEntirely()
    {
        fixture.Generator.Embed = ThermalConceptVector;
        var (space, _) = await SeedSpaceAsync();
        var restrictedTitle = $"Classified-{Guid.NewGuid():N}";
        var restricted = await SeedPageAsync(space, "classified", restrictedTitle,
            $"# Secret\n\n{Filler("heat shielding for the classified vehicle")}\n", restrictToGroup: "thermal-program");
        var visible = await SeedPageAsync(space, "public", "Public tiles",
            $"# Public\n\n{Filler("heat tiles overview")}\n");

        var run = await fixture.RunIndexerAsync();
        Assert.Equal(0, run.PagesFailed);

        // Sanity for the adversarial premise: the restricted page's chunks ARE in the
        // store (embedding is permission-blind; filtering happens at query time, §6.7).
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            Assert.True(db.PageEmbeddings.Any(e => e.PageId == restricted.Id));
        }

        var outsider = CreateUserClient(); // no thermal-program group
        using var response = await outsider.PostGraphQLAsync(SpaSearchDocument("thermal", space.Key));

        var search = response.RootElement.GetProperty("data").GetProperty("search");
        Assert.Equal(1, search.GetProperty("totalCount").GetInt32());
        Assert.Equal(visible.Id.ToString(),
            search.GetProperty("edges")[0].GetProperty("node").GetProperty("page").GetProperty("id").GetString());

        // Absent means absent: no fragment of the restricted page - id, title, content -
        // anywhere in the raw response. §6.7's "no placeholder rows, no gaps".
        var raw = response.RootElement.GetRawText();
        Assert.DoesNotContain(restricted.Id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(restrictedTitle, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("classified vehicle", raw, StringComparison.OrdinalIgnoreCase);

        // And a caller holding the group sees both - proving the absence above was the
        // permission filter, not a broken vector path.
        var insider = CreateUserClient("thermal-program");
        using var insiderResponse = await insider.PostGraphQLAsync(SpaSearchDocument("thermal", space.Key));
        Assert.Equal(2, insiderResponse.RootElement.GetProperty("data").GetProperty("search")
            .GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task EmbeddingEndpointDown_PageSavesSucceed_AndSearchDegradesToKeyword()
    {
        var (space, _) = await SeedSpaceAsync();
        var marker = $"magnetoplasma{Guid.NewGuid():N}"[..20];

        fixture.Generator.ThrowOnGenerate = new HttpRequestException("endpoint unreachable");
        try
        {
            // 1. Page saves flow through the domain-event pipeline untouched - embedding
            //    is a background concern and must never break a mutation (§9.2).
            var editor = CreateUserClient();
            using var create = await editor.PostGraphQLAsync($$"""
                mutation {
                  createPage(input: {
                    spaceId: "{{space.Id}}"
                    slug: "degrade-{{Guid.NewGuid():N}}"
                    title: "Degrade Check"
                    content: "# Notes\n\n{{marker}} inspection results"
                  }) { page { id } error { __typename } }
                }
                """);
            Assert.Equal(JsonValueKind.Null,
                create.RootElement.GetProperty("data").GetProperty("createPage").GetProperty("error").ValueKind);

            // 2. The background job fails cleanly: marked for retry, nothing stored,
            //    nothing thrown out of the run.
            var run = await fixture.RunIndexerAsync();
            Assert.True(run.PagesFailed > 0);
            Assert.Equal(0, run.PagesEmbedded);
            // Not asserting Aborted: that flag now means "due pages were deliberately
            // skipped because the endpoint looked down", which needs a run of consecutive
            // failures to establish, and how many pages are due here depends on what the
            // shared fixture happened to leave behind. The degrade contract this test is
            // about is points 1 and 3 — the save succeeded and search still answers. The
            // abort heuristic itself is covered in EmbeddingIndexerTests.

            // 3. Search still answers - keyword-only (§9.2's degraded mode), same
            //    contract shape, no error surfaced to the caller.
            using var search = await editor.PostGraphQLAsync(SpaSearchDocument(marker, space.Key));
            Assert.False(search.RootElement.TryGetProperty("errors", out _));
            Assert.Equal(1, search.RootElement.GetProperty("data").GetProperty("search")
                .GetProperty("totalCount").GetInt32());
        }
        finally
        {
            fixture.Generator.ThrowOnGenerate = null;
        }
    }
}
