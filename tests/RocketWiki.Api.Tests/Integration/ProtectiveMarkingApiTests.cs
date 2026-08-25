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
/// are both refused a TOP SECRET page they hold no clearance for — the roles that exist
/// specifically to widen access do not widen this.</item>
/// <item><b>§6.7 holds for a marking denial exactly as it does for a restriction</b>: the
/// response is byte-identical to a page that does not exist, on every path a Page is
/// reachable by, while the audit row records the real reason.</item>
/// <item><b>Nothing above the caller's clearance leaks through a derived surface</b> —
/// a search snippet, a comment, an attachment's bytes, a page property, a tree node.</item>
/// </list>
/// </summary>
public sealed class ProtectiveMarkingApiTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string SecretSentinelTitle = "ZZMARKEDTITLEZZ";
    private const string SecretSentinelBody = "ZZMARKEDBODYZZ";

    private sealed record Fixture(Guid SpaceId, string SpaceKey, Guid OpenPageId, Guid SecretPageId, Guid EyesOnlyPageId, Guid CreatorId);

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
            Key = $"PMK{Guid.NewGuid():N}"[..8],
            Name = "Protective Marking Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        // SpaceAdmin for everyone, deliberately: the strongest grant the model has, so
        // every denial below is provably the marking and nothing else.
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.SpaceAdmin,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        if (!await db.AttributeDefinitions.AnyAsync(a => a.Key == "nationality"))
        {
            db.AttributeDefinitions.Add(new AttributeDefinition
            {
                Key = "nationality",
                ClaimName = "nationality",
                DisplayName = "Nationality",
                Type = AttributeValueType.StringArray,
                AllowedValuesJson = """["GB","US","NZ"]""",
            });
        }

        var now = DateTime.UtcNow;
        var open = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "open", Title = "Open page",
            CurrentContent = "# Open page\n\nNozzle expansion notes.", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var secret = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "secret", Title = $"Secret {SecretSentinelTitle}",
            CurrentContent = $"# Secret\n\n{SecretSentinelBody} nozzle expansion notes.", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var eyesOnly = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "eyes-only", Title = "Eyes only page",
            CurrentContent = "# Eyes only\n\nnozzle expansion notes.", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.AddRange(open, secret, eyesOnly);
        db.PageMarkings.Add(NewMarking(secret.Id, ClassificationLevel.Secret));
        db.PageMarkings.Add(NewMarking(eyesOnly.Id, ClassificationLevel.Official, "GB"));
        await db.SaveChangesAsync();

        // A comment and an attachment on the SECRET page: both inherit its marking, and
        // both must be unreachable to an OFFICIAL caller.
        db.Comments.Add(new Comment
        {
            PageId = secret.Id, Body = $"Comment mentioning {SecretSentinelBody}",
            AuthorUserId = creator.Id, CreatedAtUtc = now,
        });
        db.Attachments.Add(new Attachment
        {
            PageId = secret.Id, FileName = "spec.txt", ContentType = "text/plain", SizeBytes = 4,
            ContentHash = new byte[32], StorageKey = $"attachments/{Guid.NewGuid():N}",
            UploadedByUserId = creator.Id, CreatedAtUtc = now,
        });
        await db.SaveChangesAsync();

        return new Fixture(space.Id, space.Key, open.Id, secret.Id, eyesOnly.Id, creator.Id);
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

    private HttpClient ClientFor(string? clearance, string[]? nationality = null, string[]? roles = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: $"pmk-{Guid.NewGuid()}", nationality: nationality, roles: roles, clearance: clearance);
        return client;
    }

    // --- The composition invariant ----------------------------------------------------

    [Theory]
    [InlineData(false)]  // an ordinary user, who already holds SpaceAdmin via the seed grant
    [InlineData(true)]   // ... and additionally the INSTANCE `admin` realm role
    public async Task ASpaceAdmin_AndAnInstanceAdmin_AreBothDeniedAnOverClassifiedPage(bool instanceAdmin)
    {
        string[]? roles = instanceAdmin ? ["admin"] : null;
        // Every caller here already holds SpaceAdmin on the space (see SeedAsync), and the
        // second case additionally holds the instance `admin` realm role. §6.5 already
        // forbids reading around a restriction; §21 says the same about a marking, and
        // this is the test that would fail if someone ever threaded an admin flag into the
        // clearance gate.
        var f = await SeedAsync();
        var client = ClientFor(clearance: null, roles: roles);

        using var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.SecretPageId}}") { id title } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
    }

    [Fact]
    public async Task AClearedCaller_SeesTheSamePage_ProvingTheDenialWasTheMarking()
    {
        var f = await SeedAsync();
        var client = ClientFor("SECRET");

        using var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.SecretPageId}}") { id title marking { level eyesOnly label } } }""");

        var page = result.RootElement.GetProperty("data").GetProperty("page");
        Assert.Equal(f.SecretPageId.ToString(), page.GetProperty("id").GetString());
        Assert.Equal("SECRET", page.GetProperty("marking").GetProperty("level").GetString());
        // The seeded marking carries the UK default, so the label is the prefixed form.
        Assert.Equal("UK SECRET", page.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task SetPageMarking_RoundTripsTheNationalPrefix_ThroughTheLabelField()
    {
        // design.md §21.12: `marking.label` is the single server-built display string, so
        // this is the contract the SPA renders from. Prefix, space, level, then caveat.
        var f = await SeedAsync();
        var client = ClientFor("SECRET", nationality: ["GB"]);

        using var set = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: SECRET, eyesOnly: ["GB"], prefix: "uk" }) {
                marking { prefix label }
                error { kind message }
              }
            }
            """);

        var marking = set.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("marking");
        Assert.Equal("UK", marking.GetProperty("prefix").GetString());
        Assert.Equal("UK SECRET [GB EYES ONLY]", marking.GetProperty("label").GetString());

        // ... and clearing it renders the bare level, with no leading space.
        using var cleared = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: SECRET, eyesOnly: ["GB"], prefix: null }) {
                marking { prefix label }
              }
            }
            """);

        var bare = cleared.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("marking");
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("prefix").ValueKind);
        Assert.Equal("SECRET [GB EYES ONLY]", bare.GetProperty("label").GetString());
    }

    [Fact]
    public async Task ThePrefix_ChangesNoAccessDecision_OverTheWire()
    {
        // The HTTP-boundary half of the invariance proof: the same page, re-prefixed, is
        // still exactly as invisible to an uncleared caller and exactly as visible to a
        // cleared one.
        var f = await SeedAsync();
        var editor = ClientFor("SECRET");
        var uncleared = ClientFor(clearance: null);

        foreach (var prefix in new[] { "\"UK\"", "null", "\"ZZNONSENSEZZ\"" })
        {
            using var set = await editor.PostGraphQLAsync($$"""
                mutation {
                  setPageMarking(input: { pageId: "{{f.SecretPageId}}", level: SECRET, eyesOnly: [], prefix: {{prefix}} }) {
                    error { kind }
                  }
                }
                """);
            Assert.Equal(
                JsonValueKind.Null,
                set.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("error").ValueKind);

            using var denied = await uncleared.PostGraphQLAsync($$"""{ page(id: "{{f.SecretPageId}}") { id } }""");
            Assert.Equal(JsonValueKind.Null, denied.RootElement.GetProperty("data").GetProperty("page").ValueKind);

            using var allowed = await editor.PostGraphQLAsync($$"""{ page(id: "{{f.SecretPageId}}") { id } }""");
            Assert.Equal(
                f.SecretPageId.ToString(),
                allowed.RootElement.GetProperty("data").GetProperty("page").GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task EyesOnly_RequiresAMatchingNationality_AndRendersItsLabelServerSide()
    {
        var f = await SeedAsync();

        var gb = ClientFor("OFFICIAL", nationality: ["GB"]);
        using var allowed = await gb.PostGraphQLAsync($$"""{ page(id: "{{f.EyesOnlyPageId}}") { id marking { label eyesOnly } } }""");
        var marking = allowed.RootElement.GetProperty("data").GetProperty("page").GetProperty("marking");
        Assert.Equal("UK OFFICIAL [GB EYES ONLY]", marking.GetProperty("label").GetString());
        Assert.Equal(["GB"], marking.GetProperty("eyesOnly").EnumerateArray().Select(e => e.GetString()));

        var nz = ClientFor("TOP_SECRET", nationality: ["NZ"]);
        using var denied = await nz.PostGraphQLAsync($$"""{ page(id: "{{f.EyesOnlyPageId}}") { id } }""");
        // Top clearance, wrong nationality: the caveat is not outranked by the level.
        Assert.Equal(JsonValueKind.Null, denied.RootElement.GetProperty("data").GetProperty("page").ValueKind);
    }

    // --- §6.7: absent, not forbidden ---------------------------------------------------

    [Fact]
    public async Task OverClassifiedAndMissing_PageResponses_AreByteIdentical_AtTheHttpBoundary()
    {
        // The marking twin of DeniedReadAuditTests' restriction version. Same status, same
        // content type, and - because neither id is echoed back - the same body bytes.
        var f = await SeedAsync();
        var client = ClientFor(clearance: null);

        var deniedResponse = await client.PostAsJsonAsync(
            "/graphql", new { query = $$"""{ page(id: "{{f.SecretPageId}}") { id title } }""" });
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
    public async Task ADeniedMarkingRead_IsAudited_WithTheClassificationReason_AndNoMarkingValueLeaksToTheCaller()
    {
        var f = await SeedAsync();
        var client = ClientFor(clearance: null);

        using var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.SecretPageId}}") { id title } }""");
        var body = result.RootElement.ToString();

        Assert.DoesNotContain(SecretSentinelTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var denial = Assert.Single(await db.AuditEvents
            .Where(e => e.SubjectId == f.SecretPageId && e.Outcome == AuditOutcome.Denied)
            .ToListAsync());

        Assert.Equal("page.view", denial.Action);
        Assert.Equal(AuditSubjectType.Page, denial.SubjectType);
        var details = JsonDocument.Parse(denial.DetailsJson!);
        Assert.Equal("classification:secret", details.RootElement.GetProperty("reason").GetString());
    }

    // --- Reach: every surface a page's content can escape through -----------------------

    [Fact]
    public async Task Search_NeverReturnsAnOverClassifiedHit_NorItsSnippet()
    {
        var f = await SeedAsync();
        var client = ClientFor(clearance: null);

        using var result = await client.PostGraphQLAsync(
            """{ search(query: "nozzle expansion") { totalCount edges { node { snippet page { id title } } } } }""");

        var connection = result.RootElement.GetProperty("data").GetProperty("search");
        var hits = connection.GetProperty("edges").EnumerateArray().ToList();
        Assert.DoesNotContain(
            hits, h => h.GetProperty("node").GetProperty("page").GetProperty("id").GetString() == f.SecretPageId.ToString());
        // Not implied by a count either (§6.7: "no gaps in ordering that imply something
        // was removed") - the classified page is not among the total.
        Assert.Equal(hits.Count, connection.GetProperty("totalCount").GetInt32());
        // Absent entirely, not merely omitted from a list whose snippet was still built:
        // the sentinels prove the excerpting code never touched the classified content.
        var serialized = result.RootElement.ToString();
        Assert.DoesNotContain(SecretSentinelTitle, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinelBody, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageTree_OmitsAnOverClassifiedPage_WithNoGapOrPlaceholder()
    {
        var f = await SeedAsync();
        var client = ClientFor(clearance: null, nationality: ["GB"]);

        using var result = await client.PostGraphQLAsync(
            $$"""{ pageTree(spaceId: "{{f.SpaceId}}") { id title marking { level eyesOnly prefix label } } }""");

        var nodes = result.RootElement.GetProperty("data").GetProperty("pageTree").EnumerateArray().ToList();
        var ids = nodes.Select(n => n.GetProperty("id").GetString()).ToList();

        Assert.Contains(f.OpenPageId.ToString(), ids);
        Assert.Contains(f.EyesOnlyPageId.ToString(), ids); // GB national, UK OFFICIAL [GB EYES ONLY]
        Assert.DoesNotContain(f.SecretPageId.ToString(), ids);
        Assert.DoesNotContain(SecretSentinelTitle, result.RootElement.ToString(), StringComparison.Ordinal);

        // Every visible node carries its marking, so the tree badge has a data source
        // without a second query per node (design.md §21.9).
        var eyesOnlyNode = nodes.Single(n => n.GetProperty("id").GetString() == f.EyesOnlyPageId.ToString());
        Assert.Equal("UK OFFICIAL [GB EYES ONLY]", eyesOnlyNode.GetProperty("marking").GetProperty("label").GetString());
        Assert.All(nodes, n => Assert.NotEqual(
            JsonValueKind.Null, n.GetProperty("marking").GetProperty("label").ValueKind));
    }

    [Fact]
    public async Task CommentsAndAttachments_InheritThePagesMarking_AndAreUnreachableThroughIt()
    {
        // Comments and attachments carry no marking of their own, only the page's - so the
        // page being absent is what makes them absent. If the page ever resolved for an
        // uncleared caller, this would be the leak.
        var f = await SeedAsync();
        var client = ClientFor(clearance: null);

        using var result = await client.PostGraphQLAsync($$"""
            { page(id: "{{f.SecretPageId}}") { comments { body } attachments { fileName } properties { key value } } }
            """);

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        Assert.DoesNotContain(SecretSentinelBody, result.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachmentDownload_OnAnOverClassifiedPage_Is404_IdenticalToAMissingAttachment()
    {
        var f = await SeedAsync();
        Guid attachmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            attachmentId = await db.Attachments.Where(a => a.PageId == f.SecretPageId).Select(a => a.Id).SingleAsync();
        }

        var client = ClientFor(clearance: null);

        var denied = await client.GetAsync($"/attachments/{attachmentId}");
        var missing = await client.GetAsync($"/attachments/{Guid.NewGuid()}");

        Assert.Equal(missing.StatusCode, denied.StatusCode);
        Assert.Equal(await missing.Content.ReadAsByteArrayAsync(), await denied.Content.ReadAsByteArrayAsync());

        // ... and a cleared caller gets past the authorization gate (the blob itself is
        // absent from storage in this fixture, so the interesting fact is that the
        // response is no longer the not-found shape an uncleared caller sees).
        var cleared = ClientFor("SECRET");
        var clearedResponse = await cleared.GetAsync($"/attachments/{attachmentId}");
        Assert.NotEqual(missing.StatusCode, clearedResponse.StatusCode);
    }

    [Fact]
    public async Task McpGetPage_OnAnOverClassifiedPage_ReturnsNothing()
    {
        var f = await SeedAsync();
        var client = ClientFor(clearance: null);

        var response = await client.PostAsync(
            "/mcp", McpToolCall("get_page", "{\"pageId\":\"" + f.SecretPageId + "\"}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(SecretSentinelTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinelBody, body, StringComparison.Ordinal);
    }

    private static HttpContent McpToolCall(string toolName, string argumentsJson)
    {
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName + "\",\"arguments\":" + argumentsJson + "}}";
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        content.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        return content;
    }

    // --- Setting a marking over the wire -------------------------------------------------

    [Fact]
    public async Task SetPageMarking_RaisesTheMarking_AndTheAuditRowNamesTheAction()
    {
        var f = await SeedAsync();
        var client = ClientFor("SECRET");

        using var result = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: SECRET, eyesOnly: [] }) {
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
    public async Task SetPageMarking_AboveYourOwnClearance_ReturnsATypedErrorAndChangesNothing()
    {
        var f = await SeedAsync();
        var client = ClientFor("OFFICIAL");

        using var result = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: TOP_SECRET, eyesOnly: [] }) {
                marking { level }
                error { kind message }
              }
            }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("setPageMarking");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("marking").ValueKind);
        Assert.Equal("Forbidden", payload.GetProperty("error").GetProperty("kind").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(
            ClassificationLevel.Official,
            await db.PageMarkings.Where(m => m.PageId == f.OpenPageId).Select(m => m.Level).SingleAsync());
    }

    [Fact]
    public async Task SetPageMarking_ACountryOutsideTheRegisteredNationalityVocabulary_IsAValidationError()
    {
        var f = await SeedAsync();
        var client = ClientFor("SECRET", nationality: ["GB"]);

        using var result = await client.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{f.OpenPageId}}", level: OFFICIAL, eyesOnly: ["ZZ"] }) {
                error { kind message }
              }
            }
            """);

        var error = result.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("error");
        Assert.Equal("Validation", error.GetProperty("kind").GetString());
        Assert.Contains("nationality", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }
}
