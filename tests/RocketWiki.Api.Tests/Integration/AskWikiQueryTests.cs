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
/// The askWiki root field end to end (design.md §9's assistant, §6.7/§7/§15):
/// real SQLite-backed retrieval (real SearchService LIKE fallback, real
/// PageReadService canView) under a fake chat endpoint.
///
/// The adversarial centerpiece: a restricted page whose CONTENT holds a sentinel
/// that never appears in any search result surface — the only way it could reach
/// the model is through the ask pipeline's own content loading. The FakeChatClient
/// records every message verbatim, and the test asserts the sentinel is absent from
/// that transcript for a non-cleared asker and present for a cleared one. That is
/// the strongest available proof that retrieval ran under the caller's principal:
/// the model cannot leak what it was never sent.
///
/// The factory is shared per class, so every test seeds its own space with unique
/// keys/terms and resets the fake client's script — no cross-test bleed.
/// </summary>
public sealed class AskWikiQueryTests(AskWikiApiFixture fixture) : IClassFixture<AskWikiApiFixture>
{
    private const string ClearedGroup = "askwiki-cleared";

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
            Key = $"A{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Ask Wiki Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        await db.SaveChangesAsync();
        return (space, creator);
    }

    private async Task<Page> SeedPageAsync(
        Space space, string slug, string title, string content, string? restrictToGroup = null)
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
        client.SetTestUser(sub: $"asker-{Guid.NewGuid():N}", email: "a@example.test", name: "Asker", groups: groups);
        return client;
    }

    private static async Task<JsonDocument> AskAsync(HttpClient client, string question) =>
        await client.PostGraphQLAsync($$"""
            query { askWiki(question: "{{question}}") { answer citations { pageId title headingPath anchorId } unavailable } }
            """);

    /// <summary>Seeds the adversarial pair sharing one unique term: a public page and a
    /// restricted page whose content carries a sentinel that exists nowhere else.</summary>
    private async Task<(Space Space, Page Public, Page Restricted, string Term, string RestrictedSentinel)> SeedAdversarialPairAsync()
    {
        var term = $"zzterm{Guid.NewGuid():N}"[..16];
        var restrictedSentinel = $"ZZRESTRICTED{Guid.NewGuid():N}ZZ";

        var (space, _) = await SeedSpaceAsync();
        // The intro section is padded past ChunkerOptions.TargetChars so the chunker
        // does NOT merge it with the Purge section — the citation deep-link test needs
        // the term's chunk to carry its own section attribution, not the merged
        // range's first-section attribution.
        var filler = string.Join(" ", Enumerable.Repeat("procedural filler text", 120));
        var publicPage = await SeedPageAsync(space, "public", "Umbilical Ops Guide",
            $"# Procedures\n\n{filler}\n\n## Purge\n\nThe {term} purge interval is 90 seconds.");
        var restrictedPage = await SeedPageAsync(space, "restricted", "Controlled Overrides",
            $"# Overrides\n\n{restrictedSentinel} override values for {term} operations.",
            restrictToGroup: ClearedGroup);

        return (space, publicPage, restrictedPage, term, restrictedSentinel);
    }

    [Fact]
    public async Task AskWiki_NonClearedAsker_RestrictedContentNeverReachesTheModel_AndIsNeverCited()
    {
        fixture.ChatClient.Reset();
        var (_, publicPage, restrictedPage, term, restrictedSentinel) = await SeedAdversarialPairAsync();

        var callsBefore = fixture.ChatClient.CallCount;
        using var response = await AskAsync(CreateUserClient(), term); // no groups: fails the restriction

        var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("unavailable").ValueKind);
        Assert.False(string.IsNullOrEmpty(ask.GetProperty("answer").GetString()));

        // Citations: only the public page, never the restricted one.
        var citedIds = ask.GetProperty("citations").EnumerateArray()
            .Select(c => c.GetProperty("pageId").GetString()).ToList();
        Assert.Contains(publicPage.Id.ToString(), citedIds);
        Assert.DoesNotContain(restrictedPage.Id.ToString(), citedIds);

        // The adversarial core: sweep EVERYTHING the model was ever sent. Non-vacuous
        // by construction — the public page's content did travel (proving the pipeline
        // genuinely built a context), the restricted page's never did.
        Assert.True(fixture.ChatClient.CallCount > callsBefore);
        var transcript = fixture.ChatClient.Transcript;
        Assert.Contains(term, transcript);
        Assert.Contains("purge interval is 90 seconds", transcript);
        Assert.DoesNotContain(restrictedSentinel, transcript);
        Assert.DoesNotContain("Controlled Overrides", transcript); // not even the title

        // And nothing restricted in the response body either, §6.7-style.
        var raw = response.RootElement.GetRawText();
        Assert.DoesNotContain(restrictedSentinel, raw);
        Assert.DoesNotContain("Controlled Overrides", raw);
    }

    [Fact]
    public async Task AskWiki_ClearedAsker_RetrievesAndCitesTheRestrictedPage()
    {
        fixture.ChatClient.Reset();
        var (_, _, restrictedPage, term, restrictedSentinel) = await SeedAdversarialPairAsync();

        using var response = await AskAsync(CreateUserClient(ClearedGroup), term);

        var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("unavailable").ValueKind);

        // The same pipeline that withheld the page above hands it over for a cleared
        // principal — proving the absence was the rule engine's decision, not the
        // assistant simply never loading content.
        Assert.Contains(restrictedSentinel, fixture.ChatClient.Transcript);

        var citedIds = ask.GetProperty("citations").EnumerateArray()
            .Select(c => c.GetProperty("pageId").GetString()).ToList();
        Assert.Contains(restrictedPage.Id.ToString(), citedIds);
    }

    [Fact]
    public async Task AskWiki_NotConfigured_TypedPayload_ZeroModelCalls()
    {
        fixture.ChatClient.Reset();
        var callsBefore = fixture.ChatClient.CallCount;

        // The base factory has no chat client and no options — §15 fail-closed:
        // feature absent, typed NOT_CONFIGURED, nothing retrieved, nothing sent.
        var client = fixture.BaseFactory.CreateClient();
        client.SetTestUser(sub: $"asker-{Guid.NewGuid():N}");

        using var response = await AskAsync(client, "anything at all");

        var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
        Assert.Equal("NOT_CONFIGURED", ask.GetProperty("unavailable").GetString());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("answer").ValueKind);
        Assert.Empty(ask.GetProperty("citations").EnumerateArray());
        Assert.Equal(callsBefore, fixture.ChatClient.CallCount);
    }

    [Fact]
    public async Task AskWiki_ModelEndpointFailure_DegradesToTypedUnreachable()
    {
        fixture.ChatClient.Reset();
        var (_, _, _, term, _) = await SeedAdversarialPairAsync();

        fixture.ChatClient.ThrowOnCall = new HttpRequestException("connection refused");
        try
        {
            using var response = await AskAsync(CreateUserClient(), term);

            var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
            Assert.Equal("UNREACHABLE", ask.GetProperty("unavailable").GetString());
            Assert.Equal(JsonValueKind.Null, ask.GetProperty("answer").ValueKind);
            Assert.Empty(ask.GetProperty("citations").EnumerateArray());
        }
        finally
        {
            fixture.ChatClient.Reset();
        }
    }

    [Fact]
    public async Task AskWiki_NoRetrievableContent_TypedNoResults_ModelNeverCalled()
    {
        fixture.ChatClient.Reset();
        await SeedSpaceAsync(); // a space exists; the term matches nothing
        var callsBefore = fixture.ChatClient.CallCount;

        using var response = await AskAsync(CreateUserClient(), $"zznothing{Guid.NewGuid():N}");

        var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
        Assert.Equal("NO_RESULTS", ask.GetProperty("unavailable").GetString());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("answer").ValueKind);
        Assert.Empty(ask.GetProperty("citations").EnumerateArray());

        // Fail closed on grounding: with nothing viewable to ground an answer in,
        // the model is never invoked at all.
        Assert.Equal(callsBefore, fixture.ChatClient.CallCount);
    }

    [Fact]
    public async Task AskWiki_FabricatedCitationMarkers_AreDroppedFromAnswerAndCitations()
    {
        fixture.ChatClient.Reset();
        var (_, publicPage, _, term, _) = await SeedAdversarialPairAsync();

        fixture.ChatClient.Respond = _ => "See [S1] and also [S99].";
        try
        {
            using var response = await AskAsync(CreateUserClient(), term);

            var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
            var answer = ask.GetProperty("answer").GetString()!;

            // [S1] maps to real retrieved context and survives; [S99] was never
            // issued — the model can only cite what it was given — so it is stripped
            // from the answer and yields no citation.
            Assert.Contains("[S1]", answer);
            Assert.DoesNotContain("[S99]", answer);

            var citations = ask.GetProperty("citations").EnumerateArray().ToList();
            var citation = Assert.Single(citations);
            Assert.Equal(publicPage.Id.ToString(), citation.GetProperty("pageId").GetString());
            Assert.Equal("Umbilical Ops Guide", citation.GetProperty("title").GetString());
        }
        finally
        {
            fixture.ChatClient.Reset();
        }
    }

    [Fact]
    public async Task AskWiki_CitationsCarryTheSectionDeepLink()
    {
        fixture.ChatClient.Reset();
        var (_, publicPage, _, term, _) = await SeedAdversarialPairAsync();

        using var response = await AskAsync(CreateUserClient(), term);

        var ask = response.RootElement.GetProperty("data").GetProperty("askWiki");
        var citation = ask.GetProperty("citations").EnumerateArray()
            .First(c => c.GetProperty("pageId").GetString() == publicPage.Id.ToString());

        // The chunker attributes the term's section per the shared anchor contract
        // (§9): breadcrumb + anchor id, so the SPA can deep-link the citation.
        Assert.Equal(["Procedures", "Purge"],
            citation.GetProperty("headingPath").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("procedures--purge", citation.GetProperty("anchorId").GetString());
    }

    [Fact]
    public async Task AskWiki_AuditRow_CarriesQuestionRetrievedAndCitedPageIds()
    {
        fixture.ChatClient.Reset();
        var (_, publicPage, restrictedPage, term, _) = await SeedAdversarialPairAsync();

        using var response = await AskAsync(CreateUserClient(ClearedGroup), term);
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("data").GetProperty("askWiki")
            .GetProperty("unavailable").ValueKind);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        // §7, the search.query precedent: exactly one assistant.ask row per ask, its
        // Details carrying the question text plus which pages' content traveled to
        // the model and which came back cited — the audit table, not telemetry, is
        // where who-asked-what lives.
        var row = Assert.Single(await db.AuditEvents
            .Where(e => e.Action == "assistant.ask" && e.DetailsJson!.Contains(term))
            .ToListAsync());

        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Equal(AuditChannel.GraphQl, row.Channel);
        Assert.NotNull(row.UserId);

        using var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal(term, details.RootElement.GetProperty("question").GetString());
        Assert.Equal("answered", details.RootElement.GetProperty("disposition").GetString());

        var retrieved = details.RootElement.GetProperty("retrievedPageIds").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains(publicPage.Id.ToString(), retrieved);
        Assert.Contains(restrictedPage.Id.ToString(), retrieved);

        var cited = details.RootElement.GetProperty("citedPageIds").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains(publicPage.Id.ToString(), cited);
    }

    [Fact]
    public async Task AskWiki_UnavailableAsk_StillAudited_AsSuccessWithDisposition()
    {
        fixture.ChatClient.Reset();
        var question = $"zznothing{Guid.NewGuid():N}";

        using var response = await AskAsync(CreateUserClient(), question);
        Assert.Equal("NO_RESULTS", response.RootElement.GetProperty("data").GetProperty("askWiki")
            .GetProperty("unavailable").GetString());

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        // The gitlab.fetch reasoning: an unavailable result is still a completed ask;
        // Outcome stays Success (denied is reserved for ABAC refusals) and the
        // disposition lives in Details.
        var row = Assert.Single(await db.AuditEvents
            .Where(e => e.Action == "assistant.ask" && e.DetailsJson!.Contains(question))
            .ToListAsync());
        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Contains("no_results", row.DetailsJson);
    }

    [Fact]
    public async Task AskWiki_Anonymous_IsRefused_WithZeroModelCalls()
    {
        fixture.ChatClient.Reset();
        var callsBefore = fixture.ChatClient.CallCount;

        var client = fixture.Factory.CreateClient(); // no test-user header: anonymous

        using var response = await AskAsync(client, "anything");

        // Refused loudly (no anonymous asks), before any retrieval or model work.
        var errors = response.RootElement.GetProperty("errors");
        Assert.Contains("AUTH_NOT_AUTHENTICATED", response.RootElement.GetRawText());
        Assert.True(errors.GetArrayLength() > 0);
        Assert.Equal(callsBefore, fixture.ChatClient.CallCount);
    }
}
