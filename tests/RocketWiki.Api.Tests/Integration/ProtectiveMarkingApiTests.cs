using System.Net.Http.Json;
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
/// design.md §21 at the HTTP boundary. Three things are being proven here that no lower
/// tier can prove:
///
/// <list type="number">
/// <item><b>The composition invariant end to end.</b> An instance admin and a space admin
/// are both refused a page carrying a selector no grant confers on them — the roles that
/// exist specifically to widen access do not widen this.</item>
/// <item><b>§6.7 holds for a marking denial exactly as it does for a restriction</b>: the
/// response is byte-identical to a page that does not exist, on every path a Page is
/// reachable by, while the audit row records the real reason.</item>
/// <item><b>Nothing the caller is not granted leaks through a derived surface</b> —
/// a search snippet, a comment, an attachment's bytes, a page property, a tree node.</item>
/// </list>
///
/// <para>The denying fact used to be the level: a SECRET page and a caller with no
/// clearance. This deployment carries no clearance attribute, so the level gates nobody
/// (§21.12) and the compartment page here is <c>UK SECRET APPLE</c> — the SECRET stays on
/// it to prove it changes nothing, and the APPLE selector, conferred only on
/// <c>apple-readers</c>, is what denies.</para>
/// </summary>
public sealed class ProtectiveMarkingApiTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string SecretSentinelTitle = "ZZMARKEDTITLEZZ";
    private const string SecretSentinelBody = "ZZMARKEDBODYZZ";
    private const string AppleReaders = "apple-readers";

    private sealed record Fixture(Guid SpaceId, string SpaceKey, Guid OpenPageId, Guid CompartmentPageId, Guid EyesOnlyPageId, Guid CreatorId);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"PMK{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Protective Marking Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        // SpaceAdmin for everyone, deliberately: the strongest grant the model has, so
        // every denial below is provably the marking and nothing else.
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = SpaceRole.SpaceAdmin,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        }));

        // The one grant that confers APPLE, and it matches apple-readers only: the whole
        // of what separates a "granted" caller from an "ungranted" one here.
        var appleGrant = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition(AppleReaders)),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        };
        appleGrant.Selectors.Add(new AccessRuleSelector { AccessRuleId = appleGrant.Id, Category = "FRUIT", Value = "APPLE" });
        db.AccessRules.Add(appleGrant);

        if (!await db.AttributeDefinitions.AnyAsync(a => a.Key == "nationality"))
        {
            db.AttributeDefinitions.Add(new AttributeDefinition
            {
                Key = "nationality",
                ClaimName = "nationality",
                DisplayName = "Nationality",
                Type = AttributeValueType.StringArray,
                AllowedValuesJson = """["UK","US","NZ"]""",
            });
        }

        var now = DateTime.UtcNow;
        var open = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "open", Title = "Open page",
            CurrentContent = "# Open page\n\nNozzle expansion notes.", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var compartment = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "compartment", Title = $"Compartment {SecretSentinelTitle}",
            CurrentContent = $"# Compartment\n\n{SecretSentinelBody} nozzle expansion notes.", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var eyesOnly = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "eyes-only", Title = "Eyes only page",
            CurrentContent = "# Eyes only\n\nnozzle expansion notes.", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.AddRange(open, compartment, eyesOnly);
        var compartmentMarking = NewMarking(compartment.Id, ClassificationLevel.Secret);
        compartmentMarking.Selectors.Add(new PageMarkingSelector { PageId = compartment.Id, Category = "FRUIT", Value = "APPLE" });
        db.PageMarkings.Add(compartmentMarking);
        db.PageMarkings.Add(NewMarking(eyesOnly.Id, ClassificationLevel.Official, "UK"));
        await db.SaveChangesAsync();

        // A comment and an attachment on the compartment page: both inherit its marking,
        // and both must be unreachable to an ungranted caller.
        db.Comments.Add(new Comment
        {
            PageId = compartment.Id, Body = $"Comment mentioning {SecretSentinelBody}",
            AuthorUserId = creator.Id, CreatedAtUtc = now,
        });
        db.Attachments.Add(new Attachment
        {
            PageId = compartment.Id, FileName = "spec.txt", ContentType = "text/plain", SizeBytes = 4,
            ContentHash = new byte[32], StorageKey = $"attachments/{Guid.NewGuid():N}",
            UploadedByUserId = creator.Id, CreatedAtUtc = now,
        });
        await db.SaveChangesAsync();

        return new Fixture(space.Id, space.Key, open.Id, compartment.Id, eyesOnly.Id, creator.Id);
    }

    private static PageMarking NewMarking(Guid pageId, ClassificationLevel level, params string[] countries)
    {
        var marking = new PageMarking { PageId = pageId, Level = level, SetAtUtc = DateTime.UtcNow };
        foreach (var country in countries)
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = pageId, CountryValue = country });
        }

        return marking;
    }

    /// <summary>A caller: <paramref name="granted"/> puts them in <c>apple-readers</c>, the
    /// group the APPLE grant matches. Nothing else about a caller decides a selector.</summary>
    private HttpClient ClientFor(bool granted, string[]? nationality = null, string[]? roles = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: $"pmk-{Guid.NewGuid()}", nationality: nationality, roles: roles, groups: granted ? [AppleReaders] : null);
        return client;
    }

    // --- The composition invariant ----------------------------------------------------

    [Theory]
    [InlineData(false)]  // an ordinary user, who already holds SpaceAdmin via the seed grant
    [InlineData(true)]   // ... and additionally the INSTANCE `admin` realm role
    public async Task ASpaceAdmin_AndAnInstanceAdmin_AreBothDeniedAnUngrantedPage(bool instanceAdmin)
    {
        string[]? roles = instanceAdmin ? ["admin"] : null;
        // Every caller here already holds SpaceAdmin on the space (see SeedAsync), and the
        // second case additionally holds the instance `admin` realm role. §6.5 already
        // forbids reading around a restriction; §21 says the same about a marking, and
        // this is the test that would fail if someone ever threaded an admin flag into the
        // marking gate.
        var f = await SeedAsync();
        var client = ClientFor(granted: false, roles: roles);

        using var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.CompartmentPageId}}") { id title } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
    }

    [Fact]
    public async Task AGrantedCaller_SeesTheSamePage_ProvingTheDenialWasTheMarking()
    {
        var f = await SeedAsync();
        var client = ClientFor(granted: true);

        using var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.CompartmentPageId}}") { id title marking { level eyesOnly label } } }""");

        var page = result.RootElement.GetProperty("data").GetProperty("page");
        Assert.Equal(f.CompartmentPageId.ToString(), page.GetProperty("id").GetString());
        Assert.Equal("SECRET", page.GetProperty("marking").GetProperty("level").GetString());
        // The seeded marking carries the UK default, so the label is the prefixed form.
        Assert.Equal("UK SECRET APPLE", page.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task TheLevel_AloneDeniesNobody_OverTheWire()
    {
        // The retired gate, pinned absent at the HTTP boundary: a page marked TOP SECRET
        // with no selector and no caveat is readable by a caller with nothing but space
        // access - there is no clearance to compare against (§21.12). If the level ever
        // gates again, this is the test that says so.
        var f = await SeedAsync();
        var editor = ClientFor(granted: true);
        var plain = ClientFor(granted: false);

        using var set = await editor.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: TOP_SECRET, eyesOnly: [], selectors: [], ukPrefix: true }) {
                marking { label }
                error { kind message }
              }
            }
            """);
        Assert.Equal(
            JsonValueKind.Null,
            set.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("error").ValueKind);

        using var read = await plain.PostGraphQLAsync($$"""{ page(id: "{{f.OpenPageId}}") { id marking { label } } }""");
        var page = read.RootElement.GetProperty("data").GetProperty("page");
        Assert.Equal(f.OpenPageId.ToString(), page.GetProperty("id").GetString());
        Assert.Equal("UK TOP SECRET", page.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task SetPageMarking_RoundTripsTheNationalPrefix_ThroughTheLabelField()
    {
        // design.md §21.12: `marking.label` is the single server-built display string, so
        // this is the contract the SPA renders from. Prefix, space, level, then caveat.
        var f = await SeedAsync();
        var client = ClientFor(granted: true, nationality: ["UK"]);

        using var set = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: SECRET, eyesOnly: [UK], selectors: [], ukPrefix: true }) {
                marking { ukPrefix label }
                error { kind message }
              }
            }
            """);

        var marking = set.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("marking");
        Assert.True(marking.GetProperty("ukPrefix").GetBoolean());
        Assert.Equal("UK SECRET UK EYES ONLY", marking.GetProperty("label").GetString());

        // ... and clearing it renders the bare level, with no leading space.
        using var cleared = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: SECRET, eyesOnly: [UK], selectors: [], ukPrefix: false }) {
                marking { ukPrefix label }
              }
            }
            """);

        var bare = cleared.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("marking");
        Assert.False(bare.GetProperty("ukPrefix").GetBoolean());
        Assert.Equal("SECRET UK EYES ONLY", bare.GetProperty("label").GetString());
    }

    [Fact]
    public async Task ThePrefix_ChangesNoAccessDecision_OverTheWire()
    {
        // The HTTP-boundary half of the invariance proof: the same page, re-prefixed, is
        // still exactly as invisible to an ungranted caller and exactly as visible to a
        // granted one.
        var f = await SeedAsync();
        var editor = ClientFor(granted: true);
        var ungranted = ClientFor(granted: false);

        foreach (var prefix in new[] { "true", "false" })
        {
            using var set = await editor.PostGraphQLAsync($$"""
                mutation {
                  setPageMarking(input: { pageId: "{{f.CompartmentPageId}}", level: SECRET, eyesOnly: [], selectors: [{ category: "FRUIT", value: "APPLE" }], ukPrefix: {{prefix}} }) {
                    error { kind }
                  }
                }
                """);
            Assert.Equal(
                JsonValueKind.Null,
                set.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("error").ValueKind);

            using var denied = await ungranted.PostGraphQLAsync($$"""{ page(id: "{{f.CompartmentPageId}}") { id } }""");
            Assert.Equal(JsonValueKind.Null, denied.RootElement.GetProperty("data").GetProperty("page").ValueKind);

            using var allowed = await editor.PostGraphQLAsync($$"""{ page(id: "{{f.CompartmentPageId}}") { id } }""");
            Assert.Equal(
                f.CompartmentPageId.ToString(),
                allowed.RootElement.GetProperty("data").GetProperty("page").GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task EyesOnly_RequiresAMatchingNationality_AndRendersItsLabelServerSide()
    {
        var f = await SeedAsync();

        var gb = ClientFor(granted: false, nationality: ["UK"]);
        using var allowed = await gb.PostGraphQLAsync($$"""{ page(id: "{{f.EyesOnlyPageId}}") { id marking { label eyesOnly } } }""");
        var marking = allowed.RootElement.GetProperty("data").GetProperty("page").GetProperty("marking");
        Assert.Equal("UK OFFICIAL UK EYES ONLY", marking.GetProperty("label").GetString());
        Assert.Equal(["UK"], marking.GetProperty("eyesOnly").EnumerateArray().Select(e => e.GetString()));

        var nz = ClientFor(granted: true, nationality: ["NZ"]);
        using var denied = await nz.PostGraphQLAsync($$"""{ page(id: "{{f.EyesOnlyPageId}}") { id } }""");
        // Every selector granted, wrong nationality: the caveat is not outranked by anything.
        Assert.Equal(JsonValueKind.Null, denied.RootElement.GetProperty("data").GetProperty("page").ValueKind);
    }

    // --- §6.7: absent, not forbidden ---------------------------------------------------

    [Fact]
    public async Task UngrantedAndMissing_PageResponses_AreByteIdentical_AtTheHttpBoundary()
    {
        // The marking twin of DeniedReadAuditTests' restriction version. Same status, same
        // content type, and - because neither id is echoed back - the same body bytes.
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        var deniedResponse = await client.PostAsJsonAsync(
            "/graphql", new { query = $$"""{ page(id: "{{f.CompartmentPageId}}") { id title } }""" });
        var missingResponse = await client.PostAsJsonAsync(
            "/graphql", new { query = $$"""{ page(id: "{{Guid.NewGuid()}}") { id title } }""" });

        Assert.Equal(missingResponse.StatusCode, deniedResponse.StatusCode);
        Assert.Equal(
            missingResponse.Content.Headers.ContentType?.ToString(),
            deniedResponse.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            await missingResponse.Content.ReadAsStringAsync(),
            await deniedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ADeniedMarkingRead_IsAudited_WithTheSelectorReason_AndNoMarkingValueLeaksToTheCaller()
    {
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        using var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.CompartmentPageId}}") { id title } }""");
        var body = result.RootElement.ToString();

        Assert.DoesNotContain(SecretSentinelTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain("APPLE", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var denial = Assert.Single(await db.AuditEvents
            .Where(e => e.SubjectId == f.CompartmentPageId && e.Outcome == AuditOutcome.Denied)
            .ToListAsync());

        Assert.Equal("page.view", denial.Action);
        Assert.Equal(AuditSubjectType.Page, denial.SubjectType);
        var details = JsonDocument.Parse(denial.DetailsJson!);
        // The category, never the value (§15/§21.15) - and never the level, which is
        // not a reason any more.
        Assert.Equal("selector:not_granted:FRUIT", details.RootElement.GetProperty("reason").GetString());
    }

    // --- Reach: every surface a page's content can escape through -----------------------

    [Fact]
    public async Task Search_NeverReturnsAnUngrantedHit_NorItsSnippet()
    {
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        using var result = await client.PostGraphQLAsync(
            """{ search(query: "nozzle expansion") { totalCount edges { node { snippet page { id title } } } } }""");

        var connection = result.RootElement.GetProperty("data").GetProperty("search");
        var hits = connection.GetProperty("edges").EnumerateArray().ToList();
        Assert.DoesNotContain(
            hits, h => h.GetProperty("node").GetProperty("page").GetProperty("id").GetString() == f.CompartmentPageId.ToString());
        // Not implied by a count either (§6.7: "no gaps in ordering that imply something
        // was removed") - the compartment page is not among the total.
        Assert.Equal(hits.Count, connection.GetProperty("totalCount").GetInt32());
        // Absent entirely, not merely omitted from a list whose snippet was still built:
        // the sentinels prove the excerpting code never touched the content.
        var serialized = result.RootElement.ToString();
        Assert.DoesNotContain(SecretSentinelTitle, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinelBody, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageTree_ShowsAnUngrantedPage_AsAProtectedLeaf_WithItsLabel()
    {
        // design.md §6.7/§21.8: the tree DISCLOSES a page the caller is not granted - a
        // placeholder at its position carrying the marking label and the failed gate -
        // and withholds everything that identifies it: no id, no title, nothing beneath.
        var f = await SeedAsync();
        var client = ClientFor(granted: false, nationality: ["UK"]);

        using var result = await client.PostGraphQLAsync($$"""
            { pageTree(spaceId: "{{f.SpaceId}}") {
                ... on PageTreeNode { id title marking { level eyesOnly ukPrefix label } }
                ... on ProtectedTreeNode { title sortOrder denial { placeholderTitle noSpaceAccess marking { level label } reasons { gate category value } } }
              } }
            """);

        var entries = result.RootElement.GetProperty("data").GetProperty("pageTree").EnumerateArray().ToList();
        var nodes = entries.Where(e => e.TryGetProperty("id", out _)).ToList();
        var ids = nodes.Select(n => n.GetProperty("id").GetString()).ToList();
        var body = result.RootElement.ToString();

        Assert.Contains(f.OpenPageId.ToString(), ids);
        Assert.Contains(f.EyesOnlyPageId.ToString(), ids); // UK national, UK OFFICIAL UK EYES ONLY
        Assert.DoesNotContain(f.CompartmentPageId.ToString(), ids);
        Assert.DoesNotContain(f.CompartmentPageId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretSentinelTitle, body, StringComparison.Ordinal);

        // The placeholder: the constant title, the label the reader is not granted, and
        // the one gate that refused them - carrying the selector, so the SPA can say
        // which compartment, and never the level as a "required" fact.
        var placeholder = Assert.Single(entries, e => e.TryGetProperty("denial", out _));
        Assert.Equal("(protected)", placeholder.GetProperty("title").GetString());
        var denial = placeholder.GetProperty("denial");
        Assert.Equal("(protected)", denial.GetProperty("placeholderTitle").GetString());
        Assert.False(denial.GetProperty("noSpaceAccess").GetBoolean());
        Assert.Equal("UK SECRET APPLE", denial.GetProperty("marking").GetProperty("label").GetString());
        var reason = Assert.Single(denial.GetProperty("reasons").EnumerateArray());
        Assert.Equal("SELECTOR_GRANT", reason.GetProperty("gate").GetString());
        Assert.Equal("FRUIT", reason.GetProperty("category").GetString());
        Assert.Equal("APPLE", reason.GetProperty("value").GetString());

        // Every visible node carries its marking, so the tree badge has a data source
        // without a second query per node (design.md §21.9).
        var eyesOnlyNode = nodes.Single(n => n.GetProperty("id").GetString() == f.EyesOnlyPageId.ToString());
        Assert.Equal("UK OFFICIAL UK EYES ONLY", eyesOnlyNode.GetProperty("marking").GetProperty("label").GetString());
        Assert.All(nodes, n => Assert.NotEqual(
            JsonValueKind.Null, n.GetProperty("marking").GetProperty("label").ValueKind));
    }

    [Fact]
    public async Task CommentsAndAttachments_InheritThePagesMarking_AndAreUnreachableThroughIt()
    {
        // Comments and attachments carry no marking of their own, only the page's - so the
        // page being absent is what makes them absent. If the page ever resolved for an
        // ungranted caller, this would be the leak.
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        using var result = await client.PostGraphQLAsync($$"""
            { page(id: "{{f.CompartmentPageId}}") { comments { body } attachments { fileName } properties { key value } } }
            """);

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        Assert.DoesNotContain(SecretSentinelBody, result.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachmentDownload_OnAnUngrantedPage_Is404_IdenticalToAMissingAttachment()
    {
        var f = await SeedAsync();
        Guid attachmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            attachmentId = await db.Attachments.Where(a => a.PageId == f.CompartmentPageId).Select(a => a.Id).SingleAsync();
        }

        var client = ClientFor(granted: false);

        var denied = await client.GetAsync($"/attachments/{attachmentId}");
        var missing = await client.GetAsync($"/attachments/{Guid.NewGuid()}");

        Assert.Equal(missing.StatusCode, denied.StatusCode);
        Assert.Equal(await missing.Content.ReadAsByteArrayAsync(), await denied.Content.ReadAsByteArrayAsync());

        // ... and a granted caller gets past the authorization gate (the blob itself is
        // absent from storage in this fixture, so the interesting fact is that the
        // response is no longer the not-found shape an ungranted caller sees).
        var granted = ClientFor(granted: true);
        var grantedResponse = await granted.GetAsync($"/attachments/{attachmentId}");
        Assert.NotEqual(missing.StatusCode, grantedResponse.StatusCode);
    }

    [Fact]
    public async Task McpGetPage_OnAnUngrantedPage_ReturnsNothing()
    {
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        var body = await McpToolCallAsync(client, "get_page", "{\"pageId\":\"" + f.CompartmentPageId + "\"}");

        // The tool really ran and really refused (not a transport error that happens
        // to omit the sentinels): the constant not-found text is in the body.
        Assert.Contains(RocketWiki.Api.Mcp.WikiMcpTools.PageNotFoundMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinelTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinelBody, body, StringComparison.Ordinal);

        // ... and a granted caller reads it through the same call.
        var grantedBody = await McpToolCallAsync(ClientFor(granted: true), "get_page", "{\"pageId\":\"" + f.CompartmentPageId + "\"}");
        Assert.Contains(SecretSentinelBody, grantedBody, StringComparison.Ordinal);
    }

    /// <summary>One raw JSON-RPC tools/call against /mcp. The Accept header is what makes
    /// the server run the tool at all: without it the answer is a JSON-RPC "Not
    /// Acceptable" error, which contains no sentinel and once let this test pass for
    /// the wrong reason.</summary>
    private static async Task<string> McpToolCallAsync(HttpClient client, string toolName, string argumentsJson)
    {
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName + "\",\"arguments\":" + argumentsJson + "}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"MCP {toolName} answered {(int)response.StatusCode}: {body}");
        Assert.DoesNotContain("Not Acceptable", body, StringComparison.Ordinal);
        return body;
    }

    // --- Level display spellings (design.md §21.1) ---------------------------------------

    [Fact]
    public async Task ClassificationScheme_ReturnsEveryLevelInSchemeOrder_WithItsUkWrittenForm()
    {
        // The picker's data source. Without it the SPA would hard-code four display
        // spellings AND their order - a second implementation of both (§21.1). The level
        // is presentational, so every caller sees all four and none is "above" them.
        var client = ClientFor(granted: false);

        using var result = await client.PostGraphQLAsync("{ classificationScheme { level name } }");

        var levels = result.RootElement.GetProperty("data").GetProperty("classificationScheme")
            .EnumerateArray().ToList();

        Assert.Equal(
            ["OFFICIAL", "OFFICIAL_SENSITIVE", "SECRET", "TOP_SECRET"],
            levels.Select(l => l.GetProperty("level").GetString()));
        // Least sensitive first: the list order IS the scheme order, so a client never
        // needs to know that OFFICIAL sorts below SECRET.
        Assert.Equal(
            ["OFFICIAL", "OFFICIAL-SENSITIVE", "SECRET", "TOP SECRET"],
            levels.Select(l => l.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ClassificationScheme_IsEmptyForAnAnonymousCaller_LikeEveryOtherRead()
    {
        var client = factory.CreateClient();
        client.ClearTestUser();

        using var result = await client.PostGraphQLAsync("{ classificationScheme { level } }");

        Assert.Empty(result.RootElement.GetProperty("data").GetProperty("classificationScheme").EnumerateArray());
    }

    [Fact]
    public async Task PageMarking_ExposesTheLevelsOwnSpelling_SoAListBadgeNeverRendersTheEnum()
    {
        // A search row or a tree badge has nowhere to put a full label. Rendering the
        // GraphQL enum there would put a machine identifier (OFFICIAL_SENSITIVE) in front
        // of a human as though it were a marking.
        var f = await SeedAsync();
        var editor = ClientFor(granted: true);

        using var set = await editor.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: OFFICIAL_SENSITIVE, eyesOnly: [], selectors: [], ukPrefix: true }) {
                marking { levelName label }
              }
            }
            """);

        var marking = set.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("marking");
        Assert.Equal("OFFICIAL-SENSITIVE", marking.GetProperty("levelName").GetString());
        // ... and the composed label is still the whole marking, prefix included.
        Assert.Equal("UK OFFICIAL-SENSITIVE", marking.GetProperty("label").GetString());

        using var read = await editor.PostGraphQLAsync(
            $$"""{ page(id: "{{f.OpenPageId}}") { marking { levelName label } } }""");
        var onPage = read.RootElement.GetProperty("data").GetProperty("page").GetProperty("marking");
        Assert.Equal("OFFICIAL-SENSITIVE", onPage.GetProperty("levelName").GetString());
        Assert.Equal("UK OFFICIAL-SENSITIVE", onPage.GetProperty("label").GetString());
    }

    // --- Setting a marking over the wire -------------------------------------------------

    [Fact]
    public async Task SetPageMarking_RaisesTheMarking_AndTheAuditRowNamesTheAction()
    {
        var f = await SeedAsync();
        var client = ClientFor(granted: true);

        using var result = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: SECRET, eyesOnly: [], selectors: [], ukPrefix: true }) {
                marking { level label }
                error { kind message }
              }
            }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("setPageMarking");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        // The input omits `prefix`, so the schema default (UK) applies — see §21.12.
        Assert.Equal("UK SECRET", payload.GetProperty("marking").GetProperty("label").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Contains(
            await db.AuditEvents.Where(e => e.SubjectId == f.OpenPageId).ToListAsync(),
            e => e.Action == "page.marking.set" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task SetPageMarking_ToAnyLevel_IsAllowed_ByACallerWithNoAttributesAtAll()
    {
        // The retired self-lockout case, inverted over the wire: "above your own
        // clearance" used to be a typed Forbidden. There is no clearance now and the
        // level is presentational (§21.12), so a caller with nothing but the space's
        // grants may set TOP SECRET - and can still read the result.
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        using var result = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: TOP_SECRET, eyesOnly: [], selectors: [], ukPrefix: true }) {
                marking { level label }
                error { kind message }
              }
            }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("setPageMarking");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        Assert.Equal("TOP_SECRET", payload.GetProperty("marking").GetProperty("level").GetString());

        using var read = await client.PostGraphQLAsync($$"""{ page(id: "{{f.OpenPageId}}") { id } }""");
        Assert.Equal(f.OpenPageId.ToString(), read.RootElement.GetProperty("data").GetProperty("page").GetProperty("id").GetString());
    }

    [Fact]
    public async Task SetPageMarking_ToASelectorYouAreNotGranted_ReturnsATypedErrorAndChangesNothing()
    {
        // The self-lockout rule (§21.6) as it now reads: a selector you are not granted
        // here, or a caveat that excludes you. Forbidden, not validation - the input is
        // well-formed, the caller is simply not entitled to the result.
        var f = await SeedAsync();
        var client = ClientFor(granted: false);

        using var result = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: OFFICIAL, eyesOnly: [], selectors: [{ category: "FRUIT", value: "APPLE" }], ukPrefix: true }) {
                marking { level }
                error { kind message }
              }
            }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("setPageMarking");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("marking").ValueKind);
        Assert.Equal("Forbidden", payload.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("selector:not_granted:FRUIT", payload.GetProperty("error").GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Empty(await db.PageMarkingSelectors.Where(s => s.PageId == f.OpenPageId).ToListAsync());
    }

    [Fact]
    public async Task SetPageMarking_ACountryOutsideTheFixedCaveatSet_IsRejectedBySchemaValidation()
    {
        // design.md §21.4: the caveat vocabulary is the fixed five, published as the
        // NationalCaveatCountry input enum - so a token outside it never reaches the
        // service. Hot Chocolate answers a document that fails validation with 400.
        var f = await SeedAsync();
        var client = ClientFor(granted: true, nationality: ["UK"]);

        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                mutation {
                  setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: OFFICIAL, eyesOnly: [ZZ], selectors: [], ukPrefix: true }) {
                    error { kind message }
                  }
                }
                """,
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"errors\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // --- Ancestor titles (§21.5 + §21.8 + §6.4.1) --------------------------------------

    /// <summary>
    /// Restrictions accumulate down the tree; markings do not (§21.5). So a caller can
    /// legitimately view a plain child whose parent carries a selector they lack — the
    /// design says so outright ("such a child stays reachable by id and through search")
    /// — and the two surfaces that render a page's ANCESTOR restriction chain by name, the
    /// §6.6 inspector and the manage-gated restrictions listing, were handing that
    /// parent's title over with it.
    ///
    /// <para>The parent is a page this caller cannot open, cannot find by search, and
    /// cannot see in the tree. §6.4.1 already treats titles as the sensitive part of a
    /// refusal ("not which ones, since their titles may themselves be restricted"), so the
    /// title is withheld while everything the inspector exists to explain — the rule, its
    /// expression, and whether the caller passed it — still travels.</para>
    /// </summary>
    [Fact]
    public async Task AnAncestorTheCallerIsNotGranted_ContributesItsRuleButNeverItsTitle()
    {
        const string ancestorSentinel = "ZZANCESTORTITLEZZ";
        Guid parentId;
        Guid childId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var creator = new User
            {
                Subject = $"anc-{Guid.NewGuid()}", DisplayName = "Seeder",
                CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(creator);
            await db.SaveChangesAsync();

            var now = DateTime.UtcNow;
            var space = new Space
            {
                Key = $"ANC{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Ancestor Space",
                OriginInstanceId = "standalone", CreatedAtUtc = now, CreatedByUserId = creator.Id,
            };
            db.Spaces.Add(space);

            // SpaceAdmin for everyone: the caller is a rule manager, so the manage-gated
            // `restrictions` listing is reachable too and both surfaces get asserted.
            db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
            {
                Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.SpaceAdmin,
                ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
            }));

            var parent = new Page
            {
                SpaceId = space.Id, AncestorPath = "/", Slug = "sensitive-parent",
                Title = $"Parent {ancestorSentinel}", CurrentContent = "# Parent",
                CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            // One SaveChanges for the whole graph: page ids are client-generated, so the
            // child's AncestorPath can reference the parent before either row exists — and
            // an intermediate save would let RocketWikiDbContext's every-page-is-marked
            // backstop materialize a marking the explicit ones below would then collide with.
            db.Pages.Add(parent);

            var child = new Page
            {
                SpaceId = space.Id, ParentPageId = parent.Id, AncestorPath = $"/{parent.Id}/",
                Slug = "ordinary-child", Title = "Ordinary child", CurrentContent = "# Child",
                CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            db.Pages.Add(child);

            // The parent carries a selector the child does not - the ordinary way this
            // arises, since re-marking a parent never re-marks its subtree (§21.5).
            var parentMarking = NewMarking(parent.Id, ClassificationLevel.Secret);
            parentMarking.Selectors.Add(new PageMarkingSelector { PageId = parent.Id, Category = "FRUIT", Value = "APPLE" });
            db.PageMarkings.Add(parentMarking);
            db.PageMarkings.Add(NewMarking(child.Id, ClassificationLevel.Official));

            // A restriction the caller PASSES, so it cannot be what hides the parent -
            // only the marking is. It is also what puts the parent in the child's chain.
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction, PageId = parent.Id, Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
            });
            await db.SaveChangesAsync();

            parentId = parent.Id;
            childId = child.Id;
        }

        var client = ClientFor(granted: false); // no grant in this space confers APPLE

        // Premises: the child is readable, the parent is not. Without both, the
        // assertions below would prove nothing.
        using (var childRead = await client.PostGraphQLAsync($$"""{ page(id: "{{childId}}") { id } }"""))
        {
            Assert.Equal(
                childId.ToString(),
                childRead.RootElement.GetProperty("data").GetProperty("page").GetProperty("id").GetString());
        }

        using (var parentRead = await client.PostGraphQLAsync($$"""{ page(id: "{{parentId}}") { id title } }"""))
        {
            Assert.Equal(
                JsonValueKind.Null,
                parentRead.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        }

        // The inspector: the parent's rule is explained, its title is not disclosed.
        using (var inspect = await client.PostGraphQLAsync($$"""
            {
              effectivePermission(pageId: "{{childId}}") {
                canView
                viewRestrictions { pageId pageTitle expressionJson passed }
              }
            }
            """))
        {
            Assert.DoesNotContain(ancestorSentinel, inspect.RootElement.GetRawText(), StringComparison.Ordinal);

            var inspected = inspect.RootElement.GetProperty("data").GetProperty("effectivePermission");
            Assert.True(inspected.GetProperty("canView").GetBoolean());
            var rule = Assert.Single(inspected.GetProperty("viewRestrictions").EnumerateArray());
            Assert.Equal(parentId.ToString(), rule.GetProperty("pageId").GetString());
            Assert.Equal(string.Empty, rule.GetProperty("pageTitle").GetString());
            // Still explained: the rule and the caller's verdict on it are the whole point.
            Assert.True(rule.GetProperty("passed").GetBoolean());
            Assert.Contains("everyone", rule.GetProperty("expressionJson").GetString()!, StringComparison.Ordinal);
        }

        // The manage-gated listing takes the same rule - §6.5's no-read-around applies to
        // an admin reading a chain exactly as it does to anyone else.
        using (var listing = await client.PostGraphQLAsync($$"""
            { page(id: "{{childId}}") { restrictions { pageId pageTitle inherited } } }
            """))
        {
            Assert.DoesNotContain(ancestorSentinel, listing.RootElement.GetRawText(), StringComparison.Ordinal);

            var row = Assert.Single(listing.RootElement.GetProperty("data").GetProperty("page")
                .GetProperty("restrictions").EnumerateArray());
            Assert.Equal(parentId.ToString(), row.GetProperty("pageId").GetString());
            Assert.Equal(string.Empty, row.GetProperty("pageTitle").GetString());
            Assert.True(row.GetProperty("inherited").GetBoolean());
        }
    }
}
