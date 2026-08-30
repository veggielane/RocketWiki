using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RocketWiki.Api.Mcp;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8's MCP server (milestone 8), tested end to end through the official MCP
/// client SDK against the real pipeline: real streamable-HTTP transport, real
/// authorization policy, real JIT provisioning, real tools over the real services, EF
/// Core on SQLite. The claims proven here, mapped to the design:
///
/// <list type="bullet">
/// <item>Tools act as the token's user — a permitted user reads content, and the same
/// call for a page that user cannot view is <b>byte-identical</b> to one for a page
/// that does not exist (§6.7: absent, never forbidden).</item>
/// <item>Anonymous requests die at the ASP.NET Core pipeline (401 + the MCP spec's
/// resource_metadata discovery challenge) before any handler or tool code runs.</item>
/// <item>Every successful tool call writes an audit row on AuditChannel.Mcp through
/// the same IAuditSink as GraphQL (§7), attributed to the JIT-provisioned user.</item>
/// <item>Search/tree/space listings inherit permission filtering from the shared
/// service layer rather than reimplementing it (§8: same code path).</item>
/// </list>
///
/// Fixture tree (same shape as PageAdversarialLeakTests):
/// <code>
/// Space (everyone: viewer)
/// └── PageA (permitted, content mentions "turbopump")
///     ├── PageB (RESTRICTED — view requires nationality US; content mentions "turbopump")
///     └── PageD (permitted sibling)
/// </code>
/// </summary>
public sealed class McpToolTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string TestClientName = "rocketwiki-tests";

    private sealed record Fixture(Guid SpaceId, string SpaceKey, Guid PageAId, Guid PageBId, Guid PageDId);

    // ---------- helpers ----------

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"MCP{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "MCP Test Space",
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

        var now = DateTime.UtcNow;
        var pageA = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "engines", Title = "Rocket Engines",
            CurrentContent = "# Rocket Engines\n\nNotes on turbopump cavitation margins.",
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.Add(pageA);
        await db.SaveChangesAsync();

        var pageB = new Page
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = "export-controlled", Title = "Export Controlled Turbopump Data",
            CurrentContent = "Restricted turbopump details.",
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var pageD = new Page
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = "test-stands", Title = "Test Stands",
            CurrentContent = "Where engines get fired.",
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.AddRange(pageB, pageD);
        await db.SaveChangesAsync();

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            PageId = pageB.Id,
            Action = PageAction.View,
            ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });
        await db.SaveChangesAsync();

        return new Fixture(space.Id, space.Key, pageA.Id, pageB.Id, pageD.Id);
    }

    /// <summary>The official MCP client over the factory's in-memory HTTP stack. The
    /// HttpClient carries the fake-auth claims header, so every JSON-RPC POST the
    /// transport makes is an authenticated request — exactly how a real client's
    /// bearer token would ride along.</summary>
    private async Task<McpClient> CreateMcpClientAsync(
        string sub, string[]? nationality = null, string? clearance = null)
    {
        var httpClient = factory.CreateClient();
        httpClient.SetTestUser(sub: sub, nationality: nationality, clearance: clearance);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            // The server's stateless mode answers GET with 405 by design; don't open one.
            EnableStandaloneGetStream = false,
        }, httpClient);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = TestClientName, Version = "1.0.0" },
        });
    }

    private static string SingleText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static JsonElement SingleJson(CallToolResult result) =>
        JsonDocument.Parse(SingleText(result)).RootElement;

    private async Task<List<AuditEvent>> McpAuditRowsForUserAsync(string sub)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == sub);
        return await db.AuditEvents
            .Where(e => e.UserId == user.Id && e.Channel == AuditChannel.Mcp)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    private async Task<int> McpAuditRowCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents.CountAsync(e => e.Channel == AuditChannel.Mcp);
    }

    // ---------- the tool surface ----------

    [Fact]
    public async Task ListTools_ExposesExactlyTheFourReadOnlyV1Tools()
    {
        await using var client = await CreateMcpClientAsync($"mcp-list-{Guid.NewGuid()}");

        var tools = await client.ListToolsAsync();

        // design.md §8's published v1 tool names, and §17: v1 is deliberately read-only,
        // so every tool must carry the read-only annotation and nothing else may appear.
        Assert.Equal(
            ["get_page", "get_page_tree", "list_spaces", "search"],
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
    }

    [Fact]
    public void AuditRegistry_CoversExactlyTheRegisteredToolsWithTheirDeclaredActions()
    {
        // The runtime half of the audit guard (the call-tool filter consults this map
        // before running anything); AuditCoverageTests is the build-time half.
        Assert.Equal(new Dictionary<string, string?>
        {
            ["search"] = "search.query",
            ["get_page"] = "page.view",
            ["list_spaces"] = "space.browse",
            ["get_page_tree"] = "space.browse",
        }, McpToolAuditRegistry.DeclaredActionsByToolName);
    }

    // ---------- get_page ----------

    [Fact]
    public async Task GetPage_AsPermittedUser_ReturnsMarkdown_AndAuditsOnTheMcpChannel()
    {
        var f = await SeedAsync();
        var sub = $"mcp-nz-{Guid.NewGuid()}";
        await using var client = await CreateMcpClientAsync(sub, nationality: ["NZ"]);

        var result = await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = f.PageAId.ToString() });

        Assert.NotEqual(true, result.IsError);
        var page = SingleJson(result);
        Assert.Equal("Rocket Engines", page.GetProperty("title").GetString());
        Assert.Contains("turbopump cavitation", page.GetProperty("markdown").GetString());

        // §7: the read is audited — same table, same sink as GraphQL, channel-tagged,
        // attributed to the JIT-provisioned local user for this token's subject.
        var rows = await McpAuditRowsForUserAsync(sub);
        var view = Assert.Single(rows, r => r.Action == "page.view");
        Assert.Equal(AuditOutcome.Success, view.Outcome);
        Assert.Equal(AuditSubjectType.Page, view.SubjectType);
        Assert.Equal(f.PageAId, view.SubjectId);
        Assert.False(string.IsNullOrEmpty(view.RequestId));
        // The client SDK's current protocol revision sends clientInfo per request, so
        // the row identifies the agent (design.md §7's "MCP client name"). A client on
        // an older initialize-handshake revision would honestly record null here.
        Assert.Equal(TestClientName, view.McpClient);
    }

    [Fact]
    public async Task GetPage_Restricted_IsByteIdenticalToNonexistent_AndOnlyTheDenialIsAudited()
    {
        var f = await SeedAsync();
        var sub = $"mcp-nz-{Guid.NewGuid()}";
        await using var client = await CreateMcpClientAsync(sub, nationality: ["NZ"]);

        var restricted = await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = f.PageBId.ToString() });
        var nonexistent = await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = Guid.NewGuid().ToString() });
        var malformed = await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = "not-a-guid" });

        // §6.7: absent, never forbidden. Serializing the full protocol-level results
        // and comparing the bytes leaves no room for a subtle tell (different message,
        // extra field, isError differing) between "doesn't exist" and "not yours".
        var serialize = (CallToolResult r) => JsonSerializer.Serialize(r, McpJsonUtilities.DefaultOptions);
        Assert.Equal(true, restricted.IsError);
        Assert.Equal(serialize(nonexistent), serialize(restricted));
        Assert.Equal(serialize(nonexistent), serialize(malformed));
        Assert.Contains(WikiMcpTools.PageNotFoundMessage, SingleText(restricted));

        // The wire is identical, the audit log is not (§6.7/§7): the restricted read —
        // and only it — writes a Denied page.view row carrying its failing restriction
        // (same ReadDenialAudit path as Query.Page). The nonexistent and malformed ids
        // audit nothing: no access decision was made, and a Denied row for a 404 would
        // pollute the probing signal with noise.
        var rows = await McpAuditRowsForUserAsync(sub);
        var denial = Assert.Single(rows, r => r.Action == "page.view");
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(AuditSubjectType.Page, denial.SubjectType);
        Assert.Equal(f.PageBId, denial.SubjectId);
        Assert.Contains($"restriction:{f.PageBId}", denial.DetailsJson);
    }

    // ---------- anonymous ----------

    [Fact]
    public async Task Anonymous_IsRejectedAtThePipeline_WithDiscoveryChallenge_AndNoToolOrAuditActivity()
    {
        var f = await SeedAsync();
        var before = await McpAuditRowCountAsync();

        // Raw JSON-RPC POST with no credentials at all — a real tools/call payload, so
        // if the pipeline gate failed, a tool WOULD have run and audited.
        var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsync("/mcp", new StringContent(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "get_page", arguments = new { pageId = f.PageAId.ToString() } },
            }),
            Encoding.UTF8,
            "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The MCP spec's OAuth discovery shape: the 401 challenge points the client at
        // the RFC 9728 protected-resource metadata document.
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("resource_metadata=", challenge.Parameter);
        Assert.Contains("/.well-known/oauth-protected-resource", challenge.Parameter);

        Assert.Equal(before, await McpAuditRowCountAsync());
    }

    [Fact]
    public async Task ProtectedResourceMetadata_AdvertisesKeycloakAsTheAuthorizationServer()
    {
        // The PRM document itself is anonymous by design (RFC 9728) — it tells a
        // client where to authenticate and reveals no wiki data. Authority comes from
        // the same configuration Program.cs feeds the JWT bearer handler.
        const string authority = "https://keycloak.test/realms/rocketwiki";
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Keycloak:Authority"] = authority,
                })));

        var client = configured.CreateClient();
        var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var metadata = JsonDocument.Parse(body);
        Assert.True(metadata.RootElement.TryGetProperty("authorization_servers", out var servers)
            && servers.GetArrayLength() == 1
            && servers[0].GetString() == authority,
            $"Expected authorization_servers [{authority}] in PRM document, got: {body}");
        Assert.EndsWith("/mcp", metadata.RootElement.GetProperty("resource").GetString());
    }

    // ---------- search ----------

    [Fact]
    public async Task Search_FiltersByPermission_AndAuditsTheQueryOnTheMcpChannel()
    {
        var f = await SeedAsync();
        var nzSub = $"mcp-nz-{Guid.NewGuid()}";
        var usSub = $"mcp-us-{Guid.NewGuid()}";

        await using (var nzClient = await CreateMcpClientAsync(nzSub, nationality: ["NZ"]))
        {
            var result = await nzClient.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });

            Assert.NotEqual(true, result.IsError);
            var pageIds = SingleJson(result).GetProperty("hits").EnumerateArray()
                .Select(h => h.GetProperty("pageId").GetGuid()).ToList();
            Assert.Contains(f.PageAId, pageIds);
            Assert.DoesNotContain(f.PageBId, pageIds); // restricted hit is absent, not redacted
        }

        await using (var usClient = await CreateMcpClientAsync(usSub, nationality: ["US"]))
        {
            var result = await usClient.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });

            var pageIds = SingleJson(result).GetProperty("hits").EnumerateArray()
                .Select(h => h.GetProperty("pageId").GetGuid()).ToList();
            // The restriction working is only meaningful if a satisfying principal DOES
            // see the page — otherwise the negative above could be a broken search.
            Assert.Contains(f.PageBId, pageIds);
            Assert.Contains(f.PageAId, pageIds);
        }

        // §7: search.query with the query text in Details — the audit table is the
        // sanctioned record for it (and §15 keeps it out of telemetry).
        var rows = await McpAuditRowsForUserAsync(nzSub);
        var search = Assert.Single(rows, r => r.Action == "search.query");
        Assert.Equal(AuditOutcome.Success, search.Outcome);
        Assert.Contains("turbopump", search.DetailsJson);
    }

    [Fact]
    public async Task SearchAudit_HasTheSameDetailsShapeOnBothChannels()
    {
        // design.md §7: one action name, one Details shape. AnalyticsService parses
        // search.query rows without knowing which channel wrote them, so a key present
        // on one channel and absent on the other silently changes what an analytics
        // panel counts — which has already happened once here, with resultCount. The
        // MCP tool asserted parity in a comment while omitting `labels` entirely;
        // nothing compared the two rows, so the comment was the only guard.
        var f = await SeedAsync();
        var sub = $"mcp-shape-{Guid.NewGuid()}";

        await using (var mcp = await CreateMcpClientAsync(sub, nationality: ["US"]))
        {
            var result = await mcp.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });
            Assert.NotEqual(true, result.IsError);
        }

        var graphqlClient = factory.CreateClient();
        graphqlClient.SetTestUser(sub: $"gql-shape-{Guid.NewGuid()}", nationality: ["US"]);
        using var searchResponse = await graphqlClient.PostGraphQLAsync($$"""
            query { search(query: "turbopump", spaceKey: "{{f.SpaceKey}}") { totalCount } }
            """);
        Assert.False(searchResponse.RootElement.TryGetProperty("errors", out _));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var searchRows = await db.AuditEvents
            .Where(e => e.Action == "search.query")
            .OrderByDescending(e => e.Id)
            .Take(50)
            .ToListAsync();

        var mcpKeys = DetailKeys(searchRows.First(r => r.Channel == AuditChannel.Mcp).DetailsJson);
        var graphqlKeys = DetailKeys(searchRows.First(r => r.Channel == AuditChannel.GraphQl).DetailsJson);

        // Non-vacuous: both really did carry the keys analytics reads.
        Assert.Contains("resultCount", mcpKeys);
        Assert.Contains("query", mcpKeys);

        Assert.Equal(graphqlKeys, mcpKeys);
    }

    private static IReadOnlyList<string> DetailKeys(string? detailsJson)
    {
        Assert.NotNull(detailsJson);
        using var document = System.Text.Json.JsonDocument.Parse(detailsJson!);
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    // ---------- list_spaces ----------

    [Fact]
    public async Task ListSpaces_OmitsUngrantedSpaces_AndAudits()
    {
        var f = await SeedAsync();

        // A second space whose only grant our caller cannot satisfy — it must simply
        // not appear, exactly like Query.Spaces (shared SpaceReads path).
        Guid hiddenSpaceId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var creator = await db.Users.FirstAsync();
            var hidden = new Space
            {
                Key = $"HID{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "Hidden Space",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
            };
            db.Spaces.Add(hidden);
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.SpaceGrant,
                SpaceId = hidden.Id,
                Role = SpaceRole.Viewer,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["ZZ"])),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = creator.Id,
            });
            await db.SaveChangesAsync();
            hiddenSpaceId = hidden.Id;
        }

        var sub = $"mcp-nz-{Guid.NewGuid()}";
        await using var client = await CreateMcpClientAsync(sub, nationality: ["NZ"]);

        var result = await client.CallToolAsync("list_spaces");

        Assert.NotEqual(true, result.IsError);
        var spaceIds = SingleJson(result).GetProperty("spaces").EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(f.SpaceId, spaceIds);
        Assert.DoesNotContain(hiddenSpaceId, spaceIds);

        var rows = await McpAuditRowsForUserAsync(sub);
        Assert.Contains(rows, r => r.Action == "space.browse" && r.Outcome == AuditOutcome.Success);
    }

    // ---------- get_page_tree ----------

    [Fact]
    public async Task GetPageTree_PrunesRestrictedSubtrees_AndAuditsWithTheSpaceSubject()
    {
        var f = await SeedAsync();
        var sub = $"mcp-nz-{Guid.NewGuid()}";
        await using var client = await CreateMcpClientAsync(sub, nationality: ["NZ"]);

        var result = await client.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = f.SpaceKey });

        Assert.NotEqual(true, result.IsError);
        var tree = SingleJson(result);
        var root = Assert.Single(tree.GetProperty("pages").EnumerateArray());
        Assert.Equal(f.PageAId, root.GetProperty("id").GetGuid());

        // §6.7: the restricted child is absent — not flagged, not a gap, just gone.
        var childIds = root.GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([f.PageDId], childIds);

        var rows = await McpAuditRowsForUserAsync(sub);
        var browse = Assert.Single(rows, r => r.Action == "space.browse");
        Assert.Equal(AuditSubjectType.Space, browse.SubjectType);
        Assert.Equal(f.SpaceId, browse.SubjectId);
        Assert.Equal(f.SpaceKey, browse.SpaceKey);
    }

    [Fact]
    public async Task GetPageTree_UngrantedSpace_IsByteIdenticalToNonexistentSpace()
    {
        await SeedAsync();

        // A space whose only grant the caller can't satisfy...
        string ungrantedKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var creator = await db.Users.FirstAsync();
            var ungranted = new Space
            {
                Key = $"UGR{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "Ungranted",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
            };
            db.Spaces.Add(ungranted);
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.SpaceGrant,
                SpaceId = ungranted.Id,
                Role = SpaceRole.Viewer,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["ZZ"])),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = creator.Id,
            });
            await db.SaveChangesAsync();
            ungrantedKey = ungranted.Key;
        }

        await using var client = await CreateMcpClientAsync($"mcp-nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        var ungrantedResult = await client.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = ungrantedKey });
        var nonexistentResult = await client.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = "NO-SUCH-SPACE" });

        var serialize = (CallToolResult r) => JsonSerializer.Serialize(r, McpJsonUtilities.DefaultOptions);
        Assert.Equal(true, ungrantedResult.IsError);
        Assert.Equal(serialize(nonexistentResult), serialize(ungrantedResult));
        Assert.Contains(WikiMcpTools.SpaceNotFoundMessage, SingleText(ungrantedResult));
    }

    [Fact]
    public async Task GetPageTree_UngrantedSpace_AuditsDeniedSpaceBrowse_OnTheMcpChannel()
    {
        // The audit half of the byte-identical test above (design.md §6.7:
        // "indistinguishable to the caller, not to the audit log"), via the same
        // SpaceReads seam Query.Space uses on GraphQL (DeniedReadAuditTests has that
        // channel): a specific-space lookup the caller holds no role in is a refused
        // browse, so exactly one Denied space.browse row lands - with the rule
        // engine's no-space-role reason, on AuditChannel.Mcp - and never a Success
        // row, since the tool errored before McpServerConfiguration's filter would
        // write one. A nonexistent key stays unaudited (§7: no access decision exists
        // for a subject that isn't there).
        await SeedAsync();
        Guid ungrantedSpaceId;
        string ungrantedKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var creator = await db.Users.FirstAsync();
            var ungranted = new Space
            {
                Key = $"UGA{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "Ungranted Audited",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
            };
            db.Spaces.Add(ungranted); // no grants at all - no role for anyone
            await db.SaveChangesAsync();
            ungrantedSpaceId = ungranted.Id;
            ungrantedKey = ungranted.Key;
        }

        var sub = $"mcp-roleless-{Guid.NewGuid()}";
        await using var client = await CreateMcpClientAsync(sub);

        var deniedResult = await client.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = ungrantedKey });
        Assert.Equal(true, deniedResult.IsError);

        var rows = await McpAuditRowsForUserAsync(sub);
        var denial = Assert.Single(rows);
        Assert.Equal("space.browse", denial.Action);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(AuditSubjectType.Space, denial.SubjectType);
        Assert.Equal(ungrantedSpaceId, denial.SubjectId);
        Assert.Equal(AuditChannel.Mcp, denial.Channel);
        Assert.NotNull(denial.DetailsJson);
        Assert.Equal("no-space-role", JsonDocument.Parse(denial.DetailsJson).RootElement.GetProperty("reason").GetString());

        // Nonexistent-key control: same error to the caller, nothing in the log.
        var rowsBefore = await McpAuditRowCountAsync();
        var missingResult = await client.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = "NO-SUCH-SPACE" });
        Assert.Equal(true, missingResult.IsError);
        Assert.Equal(rowsBefore, await McpAuditRowCountAsync());
    }

    // ---------- protective markings on the payloads (design.md §21.13) ----------

    /// <summary>Re-marks a seeded page (which the DbContext materialized at OFFICIAL) so a
    /// tool payload has something other than the baseline to report.</summary>
    private async Task MarkAsync(Guid pageId, ClassificationLevel level, params string[] eyesOnly)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var marking = await db.PageMarkings.Include(m => m.Countries).SingleAsync(m => m.PageId == pageId);
        marking.Level = level;
        marking.Countries.Clear();
        foreach (var country in eyesOnly)
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = pageId, CountryValue = country });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPage_PayloadCarriesThePagesMarking()
    {
        // §21.13: an MCP client is typically an LLM, and an unmarked payload is exactly how
        // classified text gets summarised into an unclassified context. The marking rides
        // with the content, as the one server-rendered label string (§21.1).
        var f = await SeedAsync();
        await MarkAsync(f.PageAId, ClassificationLevel.Secret, "GB");

        await using var client = await CreateMcpClientAsync(
            $"mcp-mark-{Guid.NewGuid()}", nationality: ["GB"], clearance: "SECRET");

        var result = await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = f.PageAId.ToString() });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("UK SECRET [GB EYES ONLY]", SingleJson(result).GetProperty("marking").GetString());
    }

    [Fact]
    public async Task Search_PayloadCarriesPerHitMarkingsAndTheAggregateOverThem()
    {
        var f = await SeedAsync();
        await MarkAsync(f.PageBId, ClassificationLevel.Secret); // the US-restricted page

        // A US national cleared to SECRET sees both the OFFICIAL page and the SECRET one,
        // so the aggregate is genuinely higher than any single hit's baseline.
        await using var client = await CreateMcpClientAsync(
            $"mcp-mark-{Guid.NewGuid()}", nationality: ["US"], clearance: "SECRET");

        var result = await client.CallToolAsync("search",
            new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });

        Assert.NotEqual(true, result.IsError);
        var payload = SingleJson(result);

        var byId = payload.GetProperty("hits").EnumerateArray()
            .ToDictionary(h => h.GetProperty("pageId").GetGuid(), h => h.GetProperty("marking").GetString());
        Assert.Equal("UK OFFICIAL", byId[f.PageAId]);
        Assert.Equal("UK SECRET", byId[f.PageBId]);

        // The containing payload carries the highest of them.
        Assert.Equal("UK SECRET", payload.GetProperty("aggregateMarking").GetString());
    }

    [Fact]
    public async Task Search_AggregateCoversOnlyTheHitsTheCallerWasShown()
    {
        // The other half of the same claim: the SECRET page is invisible to this caller
        // (it is restricted to US nationals), so it cannot raise the payload's marking.
        // §6.7 verified rather than assumed.
        var f = await SeedAsync();
        await MarkAsync(f.PageBId, ClassificationLevel.Secret);

        await using var client = await CreateMcpClientAsync(
            $"mcp-mark-{Guid.NewGuid()}", nationality: ["NZ"], clearance: "TOP_SECRET");

        var result = await client.CallToolAsync("search",
            new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });

        var payload = SingleJson(result);
        var hit = Assert.Single(payload.GetProperty("hits").EnumerateArray());
        Assert.Equal(f.PageAId, hit.GetProperty("pageId").GetGuid());
        Assert.Equal("UK OFFICIAL", payload.GetProperty("aggregateMarking").GetString());
    }

    [Fact]
    public async Task Clearance_AloneHidesAPageFromEveryMcpTool()
    {
        // §21.9 names MCP explicitly, and every other MCP visibility test here hides its
        // page behind a NATIONALITY restriction — one even hands the caller TOP_SECRET so
        // the marking cannot be what excludes it. So the clearance gate itself, on the
        // channel whose client is typically an LLM, had no MCP coverage at all.
        //
        // Here the page carries no restriction whatsoever: the ONLY thing between the
        // caller and the content is the classification level against their clearance.
        var f = await SeedAsync();
        await MarkAsync(f.PageAId, ClassificationLevel.Secret);

        // No clearance claim at all — §21's fail-closed default admits OFFICIAL only.
        await using var uncleared = await CreateMcpClientAsync($"mcp-uncleared-{Guid.NewGuid()}", nationality: ["GB"]);

        var search = await uncleared.CallToolAsync("search",
            new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });
        var hitIds = SingleJson(search).GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("pageId").GetGuid()).ToList();
        Assert.DoesNotContain(f.PageAId, hitIds);

        // Absent, not redacted (§6.7): reading it directly is the same "not found" as a
        // page that does not exist.
        var direct = await uncleared.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = f.PageAId.ToString() });
        Assert.Equal(true, direct.IsError);

        var tree = await uncleared.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = f.SpaceKey });
        Assert.DoesNotContain(f.PageAId.ToString(), SingleJson(tree).GetRawText(), StringComparison.Ordinal);

        // The controlling half: the SAME page, same restrictions (none), reached by a
        // caller whose clearance admits it. Without this, the absences above could just
        // as easily mean the page was never seeded.
        await using var cleared = await CreateMcpClientAsync(
            $"mcp-cleared-{Guid.NewGuid()}", nationality: ["GB"], clearance: "SECRET");

        var clearedSearch = await cleared.CallToolAsync("search",
            new Dictionary<string, object?> { ["query"] = "turbopump", ["spaceKey"] = f.SpaceKey });
        var clearedIds = SingleJson(clearedSearch).GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("pageId").GetGuid()).ToList();
        Assert.Contains(f.PageAId, clearedIds);
    }

    [Fact]
    public async Task GetPageTree_PayloadCarriesPerNodeMarkingsAndTheAggregateOverThePrunedTree()
    {
        // The aggregate spans every depth of the payload, not just its top level: the
        // SECRET marking sits on a CHILD, and the tree as a whole must say so.
        var f = await SeedAsync();
        await MarkAsync(f.PageDId, ClassificationLevel.Secret, "GB");

        await using var client = await CreateMcpClientAsync(
            $"mcp-mark-{Guid.NewGuid()}", nationality: ["GB"], clearance: "SECRET");

        var result = await client.CallToolAsync("get_page_tree",
            new Dictionary<string, object?> { ["spaceKey"] = f.SpaceKey });

        Assert.NotEqual(true, result.IsError);
        var tree = SingleJson(result);

        var root = Assert.Single(tree.GetProperty("pages").EnumerateArray());
        Assert.Equal("UK OFFICIAL", root.GetProperty("marking").GetString());

        var child = Assert.Single(
            root.GetProperty("children").EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == f.PageDId);
        Assert.Equal("UK SECRET [GB EYES ONLY]", child.GetProperty("marking").GetString());

        Assert.Equal("UK SECRET [GB EYES ONLY]", tree.GetProperty("aggregateMarking").GetString());
    }
}
