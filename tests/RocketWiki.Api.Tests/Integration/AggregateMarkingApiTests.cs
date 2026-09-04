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
/// design.md §21.13 end to end: results carry their own marking, and the containing
/// result — an Ask answer above all — carries the aggregate over everything that fed it.
///
/// The two claims that matter most, both adversarial:
/// <list type="bullet">
/// <item><b>Retrieved beats cited.</b> A SECRET page that entered the model context but
/// earned no citation still raises the answer's marking. A marking a model could defeat
/// by declining to cite would not be a marking.</item>
/// <item><b>Aggregation widens nothing.</b> A page the asker is not granted never
/// reaches retrieval (§6.7), so it cannot contribute — verified rather than assumed, by
/// asking the same question as two principals and watching the label differ, and by
/// confirming the source stays unopenable afterwards.</item>
/// </list>
///
/// Real SQLite-backed retrieval (real SearchService LIKE fallback, real PageReadService
/// canView, real chunker) under the fake chat endpoint, exactly as AskWikiQueryTests.
/// Every test seeds its own space and its own unique search term — retrieval is global,
/// so a shared term would let one test's corpus into another's aggregate.
/// </summary>
public sealed class AggregateMarkingApiTests(AskWikiApiFixture fixture) : IClassFixture<AskWikiApiFixture>
{
    // ---------- seeding ----------

    private async Task<Space> SeedSpaceAsync()
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
            Key = $"G{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Aggregate Marking Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        await db.SaveChangesAsync();
        return space;
    }

    /// <summary>A page with an explicit marking. Level/eyes-only/prefix are seeded
    /// directly rather than through setPageMarking, so a test can build a corpus the
    /// mutation's own "you may not set a marking you could not then read" rule (§21.6)
    /// would never let a single editor assemble.</summary>
    private async Task<Page> SeedPageAsync(
        Space space, string slug, string title, string content,
        ClassificationLevel level = ClassificationLevel.Official,
        string[]? eyesOnly = null,
        string? prefix = ProtectiveMarking.DefaultPrefix)
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

        var marking = new PageMarking { PageId = page.Id, Level = level, Prefix = prefix, SetAtUtc = now };
        foreach (var country in eyesOnly ?? [])
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = country });
        }

        db.PageMarkings.Add(marking);

        await db.SaveChangesAsync();
        return page;
    }

    private HttpClient CreateClient(string[]? nationality = null, string[]? groups = null)
    {
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"agg-{Guid.NewGuid():N}", nationality: nationality, groups: groups);
        return client;
    }

    /// <summary>Puts a selector on a page and adds an access grant, matching only
    /// <paramref name="grantedToGroup"/>, that confers it - the pair a selector-gated
    /// fixture needs (design.md §21.15).</summary>
    private async Task AddSelectorAsync(Page page, string category, string value, string grantedToGroup)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var marking = await db.PageMarkings.Include(m => m.Selectors).SingleAsync(m => m.PageId == page.Id);
        marking.Selectors.Add(new PageMarkingSelector { PageId = page.Id, Category = category, Value = value });

        var space = await db.Spaces.SingleAsync(s => s.Id == page.SpaceId);
        var grant = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition(grantedToGroup)),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = space.CreatedByUserId,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = space.CreatedByUserId,
        };
        grant.Selectors.Add(new AccessRuleSelector { AccessRuleId = grant.Id, Category = category, Value = value });
        db.AccessRules.Add(grant);

        await db.SaveChangesAsync();
    }

    private static Task<JsonDocument> AskAsync(HttpClient client, string question) =>
        client.PostGraphQLAsync($$"""
            query {
              askWiki(question: "{{question}}") {
                answer
                unavailable
                aggregateMarking { label level levelName ukPrefix eyesOnlySets }
                citations { pageId title marking { label level } }
              }
            }
            """);

    private static Task<JsonDocument> SearchAsync(HttpClient client, string query, string spaceKey) =>
        client.PostGraphQLAsync($$"""
            query {
              search(query: "{{query}}", spaceKey: "{{spaceKey}}") {
                totalCount
                aggregateMarking { label level eyesOnlySets ukPrefix }
                edges { node { page { id marking { label levelName } } } }
              }
            }
            """);

    private static JsonElement Field(JsonDocument response, string field) =>
        response.RootElement.GetProperty("data").GetProperty(field);

    private static string AggregateLabel(JsonElement payload) =>
        payload.GetProperty("aggregateMarking").GetProperty("label").GetString()!;

    // ---------- ask: the headline case ----------

    [Fact]
    public async Task AskWiki_RetrievedButUncitedSecretPage_StillRaisesTheAnswersMarking()
    {
        // THE test this feature exists for. Both pages enter the model context; the model
        // cites NEITHER. If the aggregate covered only citations, this answer would arrive
        // marked OFFICIAL — text synthesized from a SECRET page, wearing a marking that
        // says it is fine to paste anywhere.
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];
        var secretSentinel = $"ZZSECRETBODY{Guid.NewGuid():N}ZZ";

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "open", "Open Notes", $"# Open\n\nThe {term} routine is documented here.");
        await SeedPageAsync(space, "closed", "Closed Notes",
            $"# Closed\n\n{secretSentinel} covers {term} in detail.", ClassificationLevel.Secret);

        fixture.ChatClient.Respond = _ => "A grounded summary with no citation markers whatsoever.";
        try
        {
            using var response = await AskAsync(CreateClient(), term);
            var ask = Field(response, "askWiki");

            // Non-vacuous: the SECRET page's body really did travel to the model.
            Assert.Contains(secretSentinel, fixture.ChatClient.Transcript);

            // Nothing was cited...
            Assert.Empty(ask.GetProperty("citations").EnumerateArray());

            // ...and the answer is SECRET anyway.
            Assert.Equal("UK SECRET", AggregateLabel(ask));
            Assert.Equal("SECRET", ask.GetProperty("aggregateMarking").GetProperty("level").GetString());
        }
        finally
        {
            fixture.ChatClient.Reset();
        }
    }

    [Fact]
    public async Task AskWiki_SingleOfficialSource_IsMarkedOfficial()
    {
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "only", "Only Source", $"# Only\n\nEverything about {term}.");

        using var response = await AskAsync(CreateClient(), term);
        var ask = Field(response, "askWiki");

        Assert.Equal(JsonValueKind.Null, ask.GetProperty("unavailable").ValueKind);
        var aggregate = ask.GetProperty("aggregateMarking");
        Assert.Equal("UK OFFICIAL", aggregate.GetProperty("label").GetString());
        Assert.Equal("OFFICIAL", aggregate.GetProperty("levelName").GetString());
        Assert.True(aggregate.GetProperty("ukPrefix").GetBoolean());
        Assert.Empty(aggregate.GetProperty("eyesOnlySets").EnumerateArray());
    }

    [Fact]
    public async Task AskWiki_TwoSourcesWithDifferentCaveats_AreListedNotMerged()
    {
        // The conjunction, over the real pipeline: an intersection would render "UK
        // SECRET" — no caveat at all — over an answer drawn from two eyes-only pages.
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "gb-only", "GB Notes", $"# GB\n\nThe {term} figures.",
            ClassificationLevel.Secret, eyesOnly: ["UK"]);
        await SeedPageAsync(space, "us-only", "US Notes", $"# US\n\nThe {term} figures, again.",
            ClassificationLevel.Secret, eyesOnly: ["US"]);

        using var response = await AskAsync(CreateClient(nationality: ["UK", "US"]), term);
        var ask = Field(response, "askWiki");

        Assert.Equal("UK SECRET UK EYES ONLY, US EYES ONLY", AggregateLabel(ask));
        Assert.Equal(2, ask.GetProperty("aggregateMarking").GetProperty("eyesOnlySets").GetArrayLength());
    }

    [Fact]
    public async Task AskWiki_EachCitation_CarriesItsOwnSourcePagesMarking()
    {
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];

        var space = await SeedSpaceAsync();
        var open = await SeedPageAsync(space, "cit-open", "Open Source", $"# Open\n\nAbout {term}.");
        var closed = await SeedPageAsync(space, "cit-closed", "Closed Source", $"# Closed\n\nAlso about {term}.",
            ClassificationLevel.Secret, eyesOnly: ["UK"]);

        // The fixture's default script cites every marker it was offered.
        using var response = await AskAsync(CreateClient(nationality: ["UK"]), term);
        var ask = Field(response, "askWiki");

        var byPageId = ask.GetProperty("citations").EnumerateArray()
            .ToDictionary(c => c.GetProperty("pageId").GetString()!, c => c);

        Assert.Equal("UK OFFICIAL",
            byPageId[open.Id.ToString()].GetProperty("marking").GetProperty("label").GetString());
        Assert.Equal("UK SECRET UK EYES ONLY",
            byPageId[closed.Id.ToString()].GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task AskWiki_NoRetrievableContent_HasNoAggregateMarking()
    {
        // Nothing shown, nothing to mark — not OFFICIAL, which would assert a judgement
        // about content that does not exist.
        fixture.ChatClient.Reset();
        await SeedSpaceAsync();

        using var response = await AskAsync(CreateClient(), $"zznothing{Guid.NewGuid():N}");
        var ask = Field(response, "askWiki");

        Assert.Equal("NO_RESULTS", ask.GetProperty("unavailable").GetString());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("aggregateMarking").ValueKind);
    }

    [Fact]
    public async Task AskWiki_NotConfigured_HasNoAggregateMarking()
    {
        var client = fixture.BaseFactory.CreateClient();
        client.SetTestUser(sub: $"agg-{Guid.NewGuid():N}");

        using var response = await AskAsync(client, "anything at all");
        var ask = Field(response, "askWiki");

        Assert.Equal("NOT_CONFIGURED", ask.GetProperty("unavailable").GetString());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("aggregateMarking").ValueKind);
    }

    [Fact]
    public async Task AskWiki_AuditRow_RecordsWhatTheAnswerWasMarkedAs()
    {
        // §21.7's reasoning applied to an answer: a marking is a single mutable row, so
        // re-deriving "what was this answer marked" from retrievedPageIds after a
        // re-marking would report today's classification for yesterday's answer.
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "audited", "Audited Source", $"# Audited\n\nAll about {term}.",
            ClassificationLevel.Secret);

        using var response = await AskAsync(CreateClient(), term);
        Assert.Equal("UK SECRET", AggregateLabel(Field(response, "askWiki")));

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var row = Assert.Single(await db.AuditEvents
            .Where(e => e.Action == "assistant.ask" && e.DetailsJson!.Contains(term))
            .ToListAsync());

        using var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal("UK SECRET", details.RootElement.GetProperty("aggregateMarking").GetString());
    }

    // ---------- ask: §6.7 — what the caller cannot see contributes nothing ----------

    [Fact]
    public async Task AskWiki_APageTheAskerIsNotGranted_ContributesNothingToTheAggregate()
    {
        // §6.7 verified, not assumed: the ungranted page never reaches retrieval, so it
        // cannot raise the label. The granted half proves the absence was the gate and
        // not the corpus. The SECRET on the closed page gates nobody (§21.12); the APPLE
        // selector, conferred on apple-readers alone, is what closes it.
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];
        var secretSentinel = $"ZZUNGRANTED{Guid.NewGuid():N}ZZ";

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "gate-open", "Open", $"# Open\n\nThe {term} basics.");
        var closed = await SeedPageAsync(space, "gate-closed", "Closed", $"# Closed\n\n{secretSentinel} and {term}.",
            ClassificationLevel.Secret);
        await AddSelectorAsync(closed, "FRUIT", "APPLE", grantedToGroup: "apple-readers");

        using (var ungranted = await AskAsync(CreateClient(), term))
        {
            Assert.Equal("UK OFFICIAL", AggregateLabel(Field(ungranted, "askWiki")));
            Assert.DoesNotContain(secretSentinel, fixture.ChatClient.Transcript);
        }

        using var granted = await AskAsync(CreateClient(groups: ["apple-readers"]), term);
        Assert.Equal("UK SECRET APPLE", AggregateLabel(Field(granted, "askWiki")));
        Assert.Contains(secretSentinel, fixture.ChatClient.Transcript);
    }

    [Fact]
    public async Task AskWiki_ACaveatTheAskerFails_ContributesNothingToTheAggregate()
    {
        // The same proof for the eyes-only half of a marking: cleared to the very top of
        // the ladder, wrong nationality. The caveated page is absent from retrieval, so
        // the label neither rises to SECRET nor mentions the caveat.
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "cav-open", "Open", $"# Open\n\nThe {term} basics.");
        await SeedPageAsync(space, "cav-closed", "Closed", $"# Closed\n\nMore {term}.",
            ClassificationLevel.Secret, eyesOnly: ["US"]);

        using var response = await AskAsync(CreateClient(nationality: ["UK"]), term);
        var ask = Field(response, "askWiki");

        Assert.Equal("UK OFFICIAL", AggregateLabel(ask));
        Assert.DoesNotContain("EYES ONLY", AggregateLabel(ask));
    }

    [Fact]
    public async Task AggregateMarking_ChangesNoAccessDecision_TheAskerStillCannotOpenWhatItExcluded()
    {
        // "The aggregate path cannot alter any access decision", behaviourally: an ask
        // runs, an aggregate is computed, and the very same principal is still refused the
        // page the aggregate did not cover — byte-identically to a page that never existed
        // (§6.7). Aggregation is labelling; it moves no verdict in either direction.
        fixture.ChatClient.Reset();
        var term = $"zzagg{Guid.NewGuid():N}"[..14];

        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "acc-open", "Open", $"# Open\n\nThe {term} basics.");
        var closed = await SeedPageAsync(space, "acc-closed", "Closed", $"# Closed\n\nMore {term}.",
            ClassificationLevel.Secret);
        // Closed by a selector the asker is not granted; the SECRET on it gates nobody.
        await AddSelectorAsync(closed, "FRUIT", "APPLE", grantedToGroup: "apple-readers");

        var client = CreateClient();

        using (var response = await AskAsync(client, term))
        {
            Assert.Equal("UK OFFICIAL", AggregateLabel(Field(response, "askWiki")));
        }

        using var afterAsk = await client.PostGraphQLAsync($$"""
            query { page(id: "{{closed.Id}}") { id title } }
            """);
        using var neverExisted = await client.PostGraphQLAsync($$"""
            query { page(id: "{{Guid.NewGuid()}}") { id title } }
            """);

        Assert.Equal(JsonValueKind.Null, Field(afterAsk, "page").ValueKind);
        Assert.Equal(
            neverExisted.RootElement.GetProperty("data").GetRawText(),
            afterAsk.RootElement.GetProperty("data").GetRawText());
    }

    // ---------- search ----------

    [Fact]
    public async Task Search_AggregateMarking_CoversTheHitsTheCallerWasShown()
    {
        var term = $"zzsrch{Guid.NewGuid():N}"[..14];
        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "s-open", "Open Result", $"# Open\n\nThe {term} routine.");
        var closed = await SeedPageAsync(space, "s-closed", "Closed Result", $"# Closed\n\nThe {term} routine, restricted.",
            ClassificationLevel.Secret);
        await AddSelectorAsync(closed, "FRUIT", "APPLE", grantedToGroup: "apple-readers");

        using (var ungranted = await SearchAsync(CreateClient(), term, space.Key))
        {
            var search = Field(ungranted, "search");
            Assert.Equal(1, search.GetProperty("totalCount").GetInt32());
            Assert.Equal("UK OFFICIAL", AggregateLabel(search));
        }

        using var granted = await SearchAsync(CreateClient(groups: ["apple-readers"]), term, space.Key);
        var grantedSearch = Field(granted, "search");
        Assert.Equal(2, grantedSearch.GetProperty("totalCount").GetInt32());
        Assert.Equal("UK SECRET APPLE", AggregateLabel(grantedSearch));
    }

    [Fact]
    public async Task Search_AggregateMarking_ListsEachDistinctCaveatAmongTheHits()
    {
        var term = $"zzsrch{Guid.NewGuid():N}"[..14];
        var space = await SeedSpaceAsync();
        await SeedPageAsync(space, "c-gb", "GB Result", $"# GB\n\nThe {term} figures.",
            ClassificationLevel.Secret, eyesOnly: ["UK"]);
        await SeedPageAsync(space, "c-us", "US Result", $"# US\n\nThe {term} figures.",
            ClassificationLevel.OfficialSensitive, eyesOnly: ["US"]);

        using var response = await SearchAsync(
            CreateClient(nationality: ["UK", "US"]), term, space.Key);
        var search = Field(response, "search");

        Assert.Equal(2, search.GetProperty("totalCount").GetInt32());
        Assert.Equal("UK SECRET UK EYES ONLY, US EYES ONLY", AggregateLabel(search));
    }

    [Fact]
    public async Task Search_NoHits_HasNoAggregateMarking()
    {
        var space = await SeedSpaceAsync();

        using var response = await SearchAsync(CreateClient(), $"zznone{Guid.NewGuid():N}", space.Key);
        var search = Field(response, "search");

        Assert.Equal(0, search.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, search.GetProperty("aggregateMarking").ValueKind);
    }

    [Fact]
    public async Task SearchHit_ReachesItsOwnPagesMarking_ThroughTheObjectLevelAuthorizedPageField()
    {
        // No marking field was added to SearchHit on purpose (§6.7): a hit reaches its
        // page through PageByIdDataLoader, and `page { marking }` is the same
        // object-level-authorized route every other Page field takes. This asserts the SPA
        // can in fact get there from a search result.
        var term = $"zzsrch{Guid.NewGuid():N}"[..14];
        var space = await SeedSpaceAsync();
        var page = await SeedPageAsync(space, "hit-marking", "Marked Result", $"# Marked\n\nThe {term} routine.",
            ClassificationLevel.OfficialSensitive, eyesOnly: ["UK"]);

        using var response = await SearchAsync(
            CreateClient(nationality: ["UK"]), term, space.Key);
        var search = Field(response, "search");

        var node = Assert.Single(search.GetProperty("edges").EnumerateArray()).GetProperty("node");
        Assert.Equal(page.Id.ToString(), node.GetProperty("page").GetProperty("id").GetString());

        var marking = node.GetProperty("page").GetProperty("marking");
        Assert.Equal("UK OFFICIAL-SENSITIVE UK EYES ONLY", marking.GetProperty("label").GetString());
        Assert.Equal("OFFICIAL-SENSITIVE", marking.GetProperty("levelName").GetString());
    }
}
