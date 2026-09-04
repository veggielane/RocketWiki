using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Mcp;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.7 / §21.8 at the HTTP boundary: a denied page is DISCLOSED as a
/// <c>(protected)</c> placeholder carrying its marking and every failing gate on exactly
/// four surfaces — <c>pageAccess</c>/<c>pageAccessBySlug</c>, the tree,
/// <c>Page.linkTargets</c> and <c>Page.parentDenial</c> — withheld to the single
/// space-access fact when no access grant admits the caller, and OMITTED everywhere else
/// (the plain <c>page</c> read, search, RQL, labels, feeds, notifications, children,
/// Ask, MCP). Plus the grants API the split access model needs (§6.4/§21.15), the
/// marking mutation's new vocabulary, and the caller's own affordance fields.
///
/// <para>The fixture is built THROUGH THE REAL MUTATIONS by an instance admin, so the
/// grants API is exercised on the way in rather than seeded around:</para>
/// <code>
/// Space: ROLE_GRANT SPACE_ADMIN everyone; ACCESS_GRANT group readers;
///        ACCESS_GRANT group apple-readers {FRUIT/APPLE}; ACCESS_GRANT group north-readers {REGION/NORTH};
///        ROLE_GRANT EDITOR group editors
/// P0  UK OFFICIAL                         (links page://P1, page://P4, page://missing)
/// ├── P4  (PAGE_RESTRICTION view: group engineering)
/// P1  UK SECRET APPLE
/// ├── P5  UK OFFICIAL
/// P2  UK OFFICIAL-SENSITIVE APPLE
/// P3  UK OFFICIAL NZ EYES ONLY
/// P6  UK SECRET APPLE NORTH NZ/US EYES ONLY
/// </code>
/// <para>Personas: Alice [readers, apple-readers, editors] UK; Bob [readers] NZ; Carol
/// [readers, apple-readers] UK; Dave no groups; Erin [editors] only (a role, no access).
/// Every persona is also a Space-admin through the <c>everyone</c> role grant — which is
/// the point of Dave and Erin: roles never supersede access (§6.4). Nobody holds a
/// clearance or a per-category claim, because this deployment has neither: the SECRET
/// on P1 and P6 gates nobody, and what separates Bob from Alice on P1 is the APPLE grant
/// alone.</para>
///
/// <para>Runs on the ask-configured fixture so the <c>askWiki</c> omission sweep runs the
/// real retrieval path against a fake model; everything else is the standard SQLite
/// tier.</para>
/// </summary>
public sealed class AccessDisclosureTests(AskWikiApiFixture fixture) : IClassFixture<AskWikiApiFixture>
{
    private const string P1Title = "Secret orbital ZZP1TITLEZZ";
    private const string P4Title = "Engineering orbital ZZP4TITLEZZ";
    private const string P6Label = "UK SECRET APPLE NORTH NZ/US EYES ONLY";

    public enum Persona { Admin, Alice, Bob, Carol, Dave, Erin }

    private sealed record Fixture(
        Guid SpaceId, string SpaceKey,
        Guid P0, Guid P1, Guid P2, Guid P3, Guid P4, Guid P5, Guid P6,
        Guid P4RuleId, Guid MissingLinkId)
    {
        public Guid Page(string key) => key switch
        {
            "P0" => P0, "P1" => P1, "P2" => P2, "P3" => P3, "P4" => P4, "P5" => P5, "P6" => P6,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory => fixture.Factory;

    // Built once per class (the fixture is shared and every test reads it); personas get
    // a fresh subject per test so audit assertions filter by user.
    private static Task<Fixture>? _fixtureTask;
    private static readonly object FixtureGate = new();

    private Task<Fixture> GetFixtureAsync()
    {
        lock (FixtureGate)
        {
            return _fixtureTask ??= BuildFixtureAsync();
        }
    }

    // ------------------------------------------------------------------ personas

    private (HttpClient Client, string Sub) ClientFor(Persona persona)
    {
        var sub = $"{persona.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";
        var client = Factory.CreateClient();
        switch (persona)
        {
            case Persona.Admin:
                client.SetTestUser(sub, name: "Fixture Admin",
                    groups: ["readers", "apple-readers", "editors", "north-readers"], roles: ["admin"],
                    nationality: ["NZ", "UK"]);
                break;
            case Persona.Alice:
                client.SetTestUser(sub, name: "Alice", groups: ["readers", "apple-readers", "editors"], nationality: ["UK"]);
                break;
            case Persona.Bob:
                client.SetTestUser(sub, name: "Bob", groups: ["readers"], nationality: ["NZ"]);
                break;
            case Persona.Carol:
                client.SetTestUser(sub, name: "Carol", groups: ["readers", "apple-readers"], nationality: ["UK"]);
                break;
            case Persona.Dave:
                client.SetTestUser(sub, name: "Dave");
                break;
            case Persona.Erin:
                client.SetTestUser(sub, name: "Erin", groups: ["editors"]);
                break;
        }

        return (client, sub);
    }

    // ------------------------------------------------------------------ GraphQL helpers

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Expr(RuleNode node) => Json(RuleExpressionSerializer.Serialize(node));

    private static async Task<(HttpStatusCode Status, string Body)> RawGraphQLAsync(HttpClient client, string query)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Data(JsonDocument doc, string field) =>
        doc.RootElement.GetProperty("data").GetProperty(field);

    private static async Task<Guid> CreateSpaceAsync(HttpClient admin, string key, string initialGrants)
    {
        using var result = await admin.PostGraphQLAsync($$"""
            mutation { createSpace(
                input: { key: "{{key}}", name: "Disclosure Space", description: null }
                initialGrants: [{{initialGrants}}]
              ) { space { id } error { kind message } } }
            """);
        var payload = Data(result, "createSpace");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        return payload.GetProperty("space").GetProperty("id").GetGuid();
    }

    private static string SelectorValuesLiteral(IEnumerable<(string Category, string Value)> values) =>
        "[" + string.Join(", ", values.Select(v => $$"""{ category: "{{v.Category}}", value: "{{v.Value}}" }""")) + "]";

    private static async Task<JsonElement> CreateAccessRuleAsync(
        HttpClient client, string kind, Guid? spaceId, Guid? pageId, string? role, string? action, RuleNode expression,
        IEnumerable<(string Category, string Value)>? selectorValues = null)
    {
        using var result = await client.PostGraphQLAsync($$"""
            mutation { createAccessRule(input: {
                kind: {{kind}}
                spaceId: {{(spaceId is null ? "null" : $"\"{spaceId}\"")}}
                pageId: {{(pageId is null ? "null" : $"\"{pageId}\"")}}
                role: {{role ?? "null"}}
                action: {{action ?? "null"}}
                expressionJson: {{Expr(expression)}}
                selectorValues: {{(selectorValues is null ? "null" : SelectorValuesLiteral(selectorValues))}}
              }) { rule { id kind role selectorValues { category value } } error { kind message } } }
            """);
        return Data(result, "createAccessRule").Clone();
    }

    private static async Task<(Guid Id, int Revision)> CreatePageAsync(
        HttpClient admin, Guid spaceId, Guid? parentId, string slug, string title, string content)
    {
        using var result = await admin.PostGraphQLAsync($$"""
            mutation { createPage(input: {
                spaceId: "{{spaceId}}", parentPageId: {{(parentId is null ? "null" : $"\"{parentId}\"")}},
                slug: "{{slug}}", title: {{Json(title)}}, content: {{Json(content)}}
              }) { page { id currentRevisionNumber } error { kind message } } }
            """);
        var payload = Data(result, "createPage");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        var page = payload.GetProperty("page");
        return (page.GetProperty("id").GetGuid(), page.GetProperty("currentRevisionNumber").GetInt32());
    }

    private static async Task UpdateContentAsync(HttpClient admin, Guid pageId, int expectedRevision, string title, string content)
    {
        using var result = await admin.PostGraphQLAsync($$"""
            mutation { updatePageContent(input: {
                pageId: "{{pageId}}", expectedRevisionNumber: {{expectedRevision}}, title: {{Json(title)}}, content: {{Json(content)}}, editSummary: null
              }) { page { id } error { kind message } } }
            """);
        Assert.Equal(JsonValueKind.Null, Data(result, "updatePageContent").GetProperty("error").ValueKind);
    }

    private static async Task MoveAsync(HttpClient admin, Guid pageId, int sortOrder)
    {
        using var result = await admin.PostGraphQLAsync($$"""
            mutation { movePage(input: { pageId: "{{pageId}}", newParentPageId: null, newSortOrder: {{sortOrder}} }) { page { id } error { kind message } } }
            """);
        Assert.Equal(JsonValueKind.Null, Data(result, "movePage").GetProperty("error").ValueKind);
    }

    private static async Task<JsonElement> SetMarkingAsync(
        HttpClient client, Guid pageId, string level, IEnumerable<string> eyesOnly,
        IEnumerable<(string Category, string Value)> selectors, bool ukPrefix = true)
    {
        using var result = await client.PostGraphQLAsync($$"""
            mutation { setPageMarking(input: {
                pageId: "{{pageId}}", level: {{level}}, eyesOnly: [{{string.Join(", ", eyesOnly)}}],
                selectors: {{SelectorValuesLiteral(selectors)}}, ukPrefix: {{(ukPrefix ? "true" : "false")}}
              }) { marking { label } error { kind message } } }
            """);
        return Data(result, "setPageMarking").Clone();
    }

    private static async Task SetMarkingOrFailAsync(
        HttpClient client, Guid pageId, string level, IEnumerable<string> eyesOnly,
        IEnumerable<(string Category, string Value)> selectors, bool ukPrefix = true)
    {
        var payload = await SetMarkingAsync(client, pageId, level, eyesOnly, selectors, ukPrefix);
        Assert.True(payload.GetProperty("error").ValueKind == JsonValueKind.Null,
            $"setPageMarking on {pageId} failed: {payload.GetProperty("error").GetRawText()}");
    }

    private const string DenialSelection = """
        placeholderTitle noSpaceAccess
        marking { level levelName ukPrefix eyesOnly label selectors { category value } }
        reasons { gate passed category value countries ruleId inherited requiredRole }
        """;

    private async Task<JsonDocument> PageAccessAsync(HttpClient client, Guid pageId) =>
        await client.PostGraphQLAsync($$"""
            { pageAccess(id: "{{pageId}}") { page { id title } denial { {{DenialSelection}} } } }
            """);

    /// <summary>One raw JSON-RPC <c>tools/call</c> against /mcp (stateless streamable
    /// HTTP), returning the response body. The Accept header is load-bearing: without
    /// it the server answers a JSON-RPC "Not Acceptable" error before any tool runs,
    /// and a sweep of that body would pass for the wrong reason.</summary>
    private static async Task<string> McpAsync(HttpClient client, string tool, string argumentsJson)
    {
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + tool + "\",\"arguments\":" + argumentsJson + "}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"MCP {tool} answered {(int)response.StatusCode}: {body}");
        Assert.DoesNotContain("Not Acceptable", body, StringComparison.Ordinal);
        return body;
    }

    private async Task<List<AuditEvent>> AuditRowsAsync(string sub, Guid subjectId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Subject == sub);
        if (user is null)
        {
            return [];
        }

        return await db.AuditEvents.Where(e => e.UserId == user.Id && e.SubjectId == subjectId).ToListAsync();
    }

    private static string Reason(AuditEvent row) =>
        JsonDocument.Parse(row.DetailsJson!).RootElement.GetProperty("reason").GetString()!;

    /// <summary>A mutation payload's <c>error</c> is null - and when it is not, the
    /// failure names it, so a fixture that cannot build says why.</summary>
    private static void AssertNoError(JsonElement payload) =>
        Assert.True(payload.GetProperty("error").ValueKind == JsonValueKind.Null,
            $"Mutation failed: {payload.GetProperty("error").GetRawText()}");

    // ------------------------------------------------------------------ the fixture

    private async Task<Fixture> BuildFixtureAsync()
    {
        var (admin, _) = ClientFor(Persona.Admin);
        var key = $"DSC{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        var spaceId = await CreateSpaceAsync(admin, key, string.Join(", ",
            $$"""{ kind: ROLE_GRANT, role: SPACE_ADMIN, expressionJson: {{Expr(new EveryoneCondition())}} }""",
            $$"""{ kind: ACCESS_GRANT, expressionJson: {{Expr(new GroupCondition("readers"))}} }"""));

        AssertNoError(await CreateAccessRuleAsync(
            admin, "ACCESS_GRANT", spaceId, null, null, null, new GroupCondition("apple-readers"), [("FRUIT", "APPLE")]));
        // Fixture-only: what lets the admin set P6's REGION/NORTH without locking
        // themselves out (§21.6). No persona is in north-readers.
        AssertNoError(await CreateAccessRuleAsync(
            admin, "ACCESS_GRANT", spaceId, null, null, null, new GroupCondition("north-readers"), [("REGION", "NORTH")]));
        AssertNoError(await CreateAccessRuleAsync(
            admin, "ROLE_GRANT", spaceId, null, "EDITOR", null, new GroupCondition("editors")));

        var missing = Guid.NewGuid();
        // Root creation order is P0, P1, P2, P3, P6; explicit sort orders below make the
        // sibling positions a fact rather than a race on ids.
        var (p0, p0Revision) = await CreatePageAsync(admin, spaceId, null, "p0", "Public orbital notes", "orbital notes for everyone");
        var (p1, _) = await CreatePageAsync(admin, spaceId, null, "p1", P1Title, "orbital secrets");
        var (p2, _) = await CreatePageAsync(admin, spaceId, null, "p2", "Apple orbital", "orbital apples");
        var (p3, _) = await CreatePageAsync(admin, spaceId, null, "p3", "Kiwi orbital", "orbital kiwis");
        var (p6, _) = await CreatePageAsync(admin, spaceId, null, "p6", "Everything orbital", "orbital hexafluoride");
        var (p4, _) = await CreatePageAsync(admin, spaceId, p0, "p4", P4Title, "orbital engineering");
        var (p5, _) = await CreatePageAsync(admin, spaceId, p1, "p5", "Child orbital", "orbital child");

        await UpdateContentAsync(admin, p0, p0Revision, "Public orbital notes",
            $"orbital notes for everyone: [one](page://{p1}) [four](page://{p4}) [gone](page://{missing}) [one again](page://{p1})");

        await MoveAsync(admin, p1, 1);
        await MoveAsync(admin, p2, 2);
        await MoveAsync(admin, p3, 3);
        await MoveAsync(admin, p6, 4);

        await SetMarkingOrFailAsync(admin, p0, "OFFICIAL", [], []);
        await SetMarkingOrFailAsync(admin, p1, "SECRET", [], [("FRUIT", "APPLE")]);
        await SetMarkingOrFailAsync(admin, p2, "OFFICIAL_SENSITIVE", [], [("FRUIT", "APPLE")]);
        await SetMarkingOrFailAsync(admin, p3, "OFFICIAL", ["NZ"], []);
        await SetMarkingOrFailAsync(admin, p5, "OFFICIAL", [], []);
        await SetMarkingOrFailAsync(admin, p6, "SECRET", ["NZ", "US"], [("FRUIT", "APPLE"), ("REGION", "NORTH")]);

        var restriction = await CreateAccessRuleAsync(
            admin, "PAGE_RESTRICTION", null, p4, null, "VIEW", new GroupCondition("engineering"));
        AssertNoError(restriction);

        return new Fixture(spaceId, key, p0, p1, p2, p3, p4, p5, p6,
            restriction.GetProperty("rule").GetProperty("id").GetGuid(), missing);
    }

    /// <summary>A page nobody else's assertions look at, for tests that mutate a marking.
    /// A child of P3 so it never shifts a root's sibling position - and re-marked plain
    /// UK OFFICIAL, because a new child inherits its parent's marking (§21.5) and P3's
    /// NZ caveat would lock every UK persona out of it.</summary>
    private async Task<Guid> CreateScratchPageAsync(Fixture f)
    {
        var (admin, _) = ClientFor(Persona.Admin);
        var (id, _) = await CreatePageAsync(admin, f.SpaceId, f.P3, $"scratch-{Guid.NewGuid():N}"[..20], "Scratch orbital", "orbital scratch");
        await SetMarkingOrFailAsync(admin, id, "OFFICIAL", [], []);
        return id;
    }

    // ================================================================== page

    [Fact]
    public async Task DeniedPage_ReturnsProtectedPlaceholder_WithMarkingAndReasons()
    {
        var f = await GetFixtureAsync();
        var (bob, sub) = ClientFor(Persona.Bob);

        using var result = await PageAccessAsync(bob, f.P1);
        var body = result.RootElement.ToString();

        var access = Data(result, "pageAccess");
        Assert.Equal(JsonValueKind.Null, access.GetProperty("page").ValueKind);
        var denial = access.GetProperty("denial");
        Assert.Equal("(protected)", denial.GetProperty("placeholderTitle").GetString());
        Assert.False(denial.GetProperty("noSpaceAccess").GetBoolean());
        // The marking is shown whole - level included, though the level gated nothing.
        Assert.Equal("UK SECRET APPLE", denial.GetProperty("marking").GetProperty("label").GetString());
        Assert.Equal("SECRET", denial.GetProperty("marking").GetProperty("level").GetString());

        var reason = Assert.Single(denial.GetProperty("reasons").EnumerateArray());
        Assert.Equal("SELECTOR_GRANT", reason.GetProperty("gate").GetString());
        Assert.False(reason.GetProperty("passed").GetBoolean());
        Assert.Equal("FRUIT", reason.GetProperty("category").GetString());
        Assert.Equal("APPLE", reason.GetProperty("value").GetString());

        Assert.DoesNotContain(P1Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain(f.P1.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AtUtc", body, StringComparison.Ordinal);

        // §7: one row, Denied, with the ladder's first failing token; never a Success
        // row for a placeholder.
        var row = Assert.Single(await AuditRowsAsync(sub, f.P1));
        Assert.Equal("page.view", row.Action);
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        Assert.Equal("selector:not_granted:FRUIT", Reason(row));
    }

    [Fact]
    public async Task MissingPage_ReturnsNull_AndAuditsNothing()
    {
        await GetFixtureAsync();
        var (bob, sub) = ClientFor(Persona.Bob);
        var missing = Guid.NewGuid();

        using var result = await PageAccessAsync(bob, missing);

        Assert.Equal(JsonValueKind.Null, Data(result, "pageAccess").ValueKind);
        Assert.Empty(await AuditRowsAsync(sub, missing));
    }

    [Theory]
    [InlineData(Persona.Dave)]
    [InlineData(Persona.Erin)]
    public async Task NoSpaceAccess_PlaceholderWithholdsMarking_ForNoGrantAndForRoleOnly(Persona persona)
    {
        // Dave holds nothing but the everyone Space-admin role; Erin holds Editor too.
        // Neither matches an ACCESS grant, and roles never supersede access (§6.4): the
        // placeholder says only that, and the marking - level, label, everything - is
        // withheld (§21.8).
        var f = await GetFixtureAsync();
        var (client, sub) = ClientFor(persona);

        using var result = await PageAccessAsync(client, f.P0);
        var body = result.RootElement.ToString();

        var denial = Data(result, "pageAccess").GetProperty("denial");
        Assert.True(denial.GetProperty("noSpaceAccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, denial.GetProperty("marking").ValueKind);
        var reason = Assert.Single(denial.GetProperty("reasons").EnumerateArray());
        Assert.Equal("SPACE_ACCESS", reason.GetProperty("gate").GetString());
        Assert.False(reason.GetProperty("passed").GetBoolean());
        Assert.DoesNotContain("OFFICIAL", body, StringComparison.Ordinal);

        var row = Assert.Single(await AuditRowsAsync(sub, f.P0));
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        Assert.Equal("no-space-access", Reason(row));
    }

    [Fact]
    public async Task Placeholder_CarriesNoIdTitleOrTimestamps()
    {
        // Two halves. The schema half: the three disclosing types expose exactly the
        // allowlisted fields, so a page id, title, slug, timestamp or expression cannot
        // be selected on a placeholder at all. The body half: a real placeholder
        // response carries none of them anywhere.
        var f = await GetFixtureAsync();
        var (bob, _) = ClientFor(Persona.Bob);

        using var introspection = await bob.PostGraphQLAsync("""
            {
              denial: __type(name: "AccessDenial") { fields { name } }
              gate: __type(name: "GateResult") { fields { name } }
              leaf: __type(name: "ProtectedTreeNode") { fields { name } }
            }
            """);
        static HashSet<string> Fields(JsonElement type) =>
            type.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToHashSet();

        Assert.Equal(
            new HashSet<string> { "placeholderTitle", "noSpaceAccess", "marking", "reasons" },
            Fields(Data(introspection, "denial")));
        Assert.Equal(
            new HashSet<string> { "gate", "passed", "category", "value", "countries", "ruleId", "inherited", "requiredRole" },
            Fields(Data(introspection, "gate")));
        Assert.Equal(new HashSet<string> { "title", "sortOrder", "denial" }, Fields(Data(introspection, "leaf")));

        using var page = await PageAccessAsync(bob, f.P1);
        using var tree = await bob.PostGraphQLAsync($$"""
            { pageTree(spaceId: "{{f.SpaceId}}") {
                ... on PageTreeNode { id title }
                ... on ProtectedTreeNode { title sortOrder denial { {{DenialSelection}} } }
              } }
            """);
        foreach (var body in new[] { page.RootElement.ToString(), tree.RootElement.ToString() })
        {
            Assert.DoesNotContain(f.P1.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(f.P5.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(P1Title, body, StringComparison.Ordinal);
            Assert.DoesNotContain("Child orbital", body, StringComparison.Ordinal);
            Assert.DoesNotContain("AtUtc", body, StringComparison.Ordinal);
            Assert.DoesNotContain("expressionJson", body, StringComparison.Ordinal);
        }

        // Nothing under a denial object is named like an identifier.
        AssertNoIdentifierProperties(Data(page, "pageAccess").GetProperty("denial"));
    }

    private static void AssertNoIdentifierProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[] { "id", "pageId", "title", "slug", "spaceId", "expressionJson" });
                AssertNoIdentifierProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoIdentifierProperties(item);
            }
        }
    }

    [Fact]
    public async Task EveryFailingGate_IsReported_AndTheAuditRowNamesTheFirst()
    {
        // Bob on P6: granted neither selector, but a NZ national (in the caveat). Every
        // failing gate is listed in ladder order; the audit row carries only the first
        // (§7's deterministic token). The SECRET level fails nobody and is not listed.
        var f = await GetFixtureAsync();
        var (bob, sub) = ClientFor(Persona.Bob);

        using var result = await PageAccessAsync(bob, f.P6);
        var denial = Data(result, "pageAccess").GetProperty("denial");
        Assert.Equal(P6Label, denial.GetProperty("marking").GetProperty("label").GetString());

        var reasons = denial.GetProperty("reasons").EnumerateArray().ToList();
        Assert.Equal(["SELECTOR_GRANT", "SELECTOR_GRANT"], reasons.Select(r => r.GetProperty("gate").GetString()));
        Assert.All(reasons, r => Assert.False(r.GetProperty("passed").GetBoolean()));
        Assert.Equal(("FRUIT", "APPLE"), (reasons[0].GetProperty("category").GetString(), reasons[0].GetProperty("value").GetString()));
        Assert.Equal(("REGION", "NORTH"), (reasons[1].GetProperty("category").GetString(), reasons[1].GetProperty("value").GetString()));
        Assert.DoesNotContain(reasons, r => r.GetProperty("gate").GetString() is "NATIONAL_CAVEAT" or "RESTRICTION" or "MARKING_UNAVAILABLE");

        var row = Assert.Single(await AuditRowsAsync(sub, f.P6));
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        Assert.Equal("selector:not_granted:FRUIT", Reason(row));
    }

    [Fact]
    public async Task SelectorGrant_IsTheOnlySelectorGate()
    {
        // There used to be a second one - eligibility, a per-category claim on the token
        // that Carol lacked. It is gone with the claim: what admits a reader to an APPLE
        // page is that an access grant they match confers APPLE, and nothing else about
        // them is consulted.
        var f = await GetFixtureAsync();

        // Bob: readers confers no APPLE - G fails, and names the category and value.
        var (bob, _) = ClientFor(Persona.Bob);
        using var bobResult = await PageAccessAsync(bob, f.P2);
        var bobReason = Assert.Single(Data(bobResult, "pageAccess").GetProperty("denial").GetProperty("reasons").EnumerateArray());
        Assert.Equal("SELECTOR_GRANT", bobReason.GetProperty("gate").GetString());
        Assert.Equal("FRUIT", bobReason.GetProperty("category").GetString());
        Assert.Equal("APPLE", bobReason.GetProperty("value").GetString());

        // Carol and Alice: granted APPLE by apple-readers - the page.
        foreach (var persona in new[] { Persona.Carol, Persona.Alice })
        {
            var (client, _) = ClientFor(persona);
            using var result = await PageAccessAsync(client, f.P2);
            Assert.Equal(f.P2, Data(result, "pageAccess").GetProperty("page").GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, Data(result, "pageAccess").GetProperty("denial").ValueKind);
        }
    }

    [Fact]
    public async Task Restriction_DenialCarriesRuleIdOnly_NoPageIdOrExpression()
    {
        var f = await GetFixtureAsync();
        var (bob, _) = ClientFor(Persona.Bob);

        using var result = await PageAccessAsync(bob, f.P4);
        var body = result.RootElement.ToString();

        var denial = Data(result, "pageAccess").GetProperty("denial");
        Assert.Equal("UK OFFICIAL", denial.GetProperty("marking").GetProperty("label").GetString());
        var reason = Assert.Single(denial.GetProperty("reasons").EnumerateArray());
        Assert.Equal("RESTRICTION", reason.GetProperty("gate").GetString());
        Assert.Equal(f.P4RuleId, reason.GetProperty("ruleId").GetGuid());
        Assert.False(reason.GetProperty("inherited").GetBoolean());

        Assert.DoesNotContain(f.P4.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("engineering", body, StringComparison.Ordinal);
        Assert.DoesNotContain(P4Title, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageAccessBySlug_DeniedIsPlaceholder_MissingIsNull()
    {
        var f = await GetFixtureAsync();
        var (bob, _) = ClientFor(Persona.Bob);

        using var denied = await bob.PostGraphQLAsync($$"""
            { pageAccessBySlug(spaceKey: "{{f.SpaceKey}}", slug: "p1") { page { id } denial { noSpaceAccess marking { label } } } }
            """);
        var access = Data(denied, "pageAccessBySlug");
        Assert.Equal(JsonValueKind.Null, access.GetProperty("page").ValueKind);
        Assert.Equal("UK SECRET APPLE", access.GetProperty("denial").GetProperty("marking").GetProperty("label").GetString());

        using var missing = await bob.PostGraphQLAsync($$"""
            { pageAccessBySlug(spaceKey: "{{f.SpaceKey}}", slug: "no-such-slug") { page { id } denial { noSpaceAccess } } }
            """);
        Assert.Equal(JsonValueKind.Null, Data(missing, "pageAccessBySlug").ValueKind);

        // The decided disclosure for the slug route (§21.8): a caller no access grant
        // admits learns that a page exists at this address and nothing else.
        var (dave, _) = ClientFor(Persona.Dave);
        using var withheld = await dave.PostGraphQLAsync($$"""
            { pageAccessBySlug(spaceKey: "{{f.SpaceKey}}", slug: "p1") { page { id } denial { noSpaceAccess marking { label } reasons { gate } } } }
            """);
        var daveDenial = Data(withheld, "pageAccessBySlug").GetProperty("denial");
        Assert.True(daveDenial.GetProperty("noSpaceAccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, daveDenial.GetProperty("marking").ValueKind);
    }

    [Fact]
    public async Task LegacyPageField_StaysNullOnDenied()
    {
        // The plain read is unchanged (§21.8): null for a denied page exactly as for a
        // missing one, while the same page is a placeholder on pageAccess.
        var f = await GetFixtureAsync();
        var (bob, _) = ClientFor(Persona.Bob);

        using var byId = await bob.PostGraphQLAsync($$"""{ page(id: "{{f.P1}}") { id title } }""");
        Assert.Equal(JsonValueKind.Null, Data(byId, "page").ValueKind);

        using var bySlug = await bob.PostGraphQLAsync($$"""{ pageBySlug(spaceKey: "{{f.SpaceKey}}", slug: "p1") { id title } }""");
        Assert.Equal(JsonValueKind.Null, Data(bySlug, "pageBySlug").ValueKind);

        // Control: the plain read still returns what Bob may see.
        using var control = await bob.PostGraphQLAsync($$"""{ page(id: "{{f.P0}}") { id } }""");
        Assert.Equal(f.P0, Data(control, "page").GetProperty("id").GetGuid());
    }

    // ================================================================== tree

    private const string TreeSelection = """
        ... on PageTreeNode { id title sortOrder hasChildren marking { label } children {
          ... on PageTreeNode { id title sortOrder }
          ... on ProtectedTreeNode { title sortOrder denial { placeholderTitle noSpaceAccess marking { label } reasons { gate ruleId inherited } } }
        } }
        ... on ProtectedTreeNode { title sortOrder denial { placeholderTitle noSpaceAccess marking { label } reasons { gate category value } } }
        """;

    [Fact]
    public async Task PageTree_DeniedNode_IsAProtectedLeaf_InPosition_WithoutChildrenIdOrTitle()
    {
        var f = await GetFixtureAsync();
        var (bob, _) = ClientFor(Persona.Bob);

        using var result = await bob.PostGraphQLAsync($$"""{ pageTree(spaceId: "{{f.SpaceId}}") { {{TreeSelection}} } }""");
        var body = result.RootElement.ToString();
        var roots = Data(result, "pageTree").EnumerateArray().ToList();

        // Bob's roots: P0 (visible), P1 (protected), P2 (protected), P3 (visible), P6
        // (protected) - every one at its sibling position.
        var p0 = roots.Single(r => r.TryGetProperty("id", out var id) && id.GetGuid() == f.P0);
        var p1 = roots.Single(r => r.GetProperty("sortOrder").GetInt32() == 1);
        var p3 = roots.Single(r => r.TryGetProperty("id", out var id) && id.GetGuid() == f.P3);
        Assert.True(roots.IndexOf(p0) < roots.IndexOf(p1) && roots.IndexOf(p1) < roots.IndexOf(p3));

        Assert.False(p1.TryGetProperty("id", out _));
        Assert.False(p1.TryGetProperty("children", out _));
        Assert.Equal("(protected)", p1.GetProperty("title").GetString());
        Assert.Equal("UK SECRET APPLE", p1.GetProperty("denial").GetProperty("marking").GetProperty("label").GetString());
        Assert.False(p1.GetProperty("denial").GetProperty("noSpaceAccess").GetBoolean());
        Assert.Equal("SELECTOR_GRANT", Assert.Single(p1.GetProperty("denial").GetProperty("reasons").EnumerateArray()).GetProperty("gate").GetString());

        var p6 = roots.Single(r => r.GetProperty("sortOrder").GetInt32() == 4);
        Assert.Equal(P6Label, p6.GetProperty("denial").GetProperty("marking").GetProperty("label").GetString());

        // P4 under P0: a protected leaf in P0's children, and P0 says it has children
        // (hasChildren agrees with children, placeholders counted).
        Assert.True(p0.GetProperty("hasChildren").GetBoolean());
        var p4 = Assert.Single(p0.GetProperty("children").EnumerateArray());
        Assert.False(p4.TryGetProperty("id", out _));
        Assert.Equal("(protected)", p4.GetProperty("title").GetString());
        var p4Reason = Assert.Single(p4.GetProperty("denial").GetProperty("reasons").EnumerateArray());
        Assert.Equal("RESTRICTION", p4Reason.GetProperty("gate").GetString());
        Assert.Equal(f.P4RuleId, p4Reason.GetProperty("ruleId").GetGuid());

        // Nothing beneath a placeholder, and nothing that names one.
        Assert.DoesNotContain(f.P1.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.P5.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.P4.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(P1Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain(P4Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Child orbital", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Persona.Dave)]
    [InlineData(Persona.Erin)]
    public async Task PageTree_NoSpaceAccess_IsEmptyAndByteIdenticalToMissing(Persona persona)
    {
        var f = await GetFixtureAsync();
        var (client, sub) = ClientFor(persona);

        var (deniedStatus, deniedBody) = await RawGraphQLAsync(client, $$"""{ pageTree(spaceId: "{{f.SpaceId}}") { {{TreeSelection}} } }""");
        var (missingStatus, missingBody) = await RawGraphQLAsync(client, $$"""{ pageTree(spaceId: "{{Guid.NewGuid()}}") { {{TreeSelection}} } }""");

        Assert.Equal(HttpStatusCode.OK, deniedStatus);
        Assert.Equal(missingStatus, deniedStatus);
        Assert.Equal(missingBody, deniedBody);

        var row = Assert.Single(await AuditRowsAsync(sub, f.SpaceId));
        Assert.Equal("space.browse", row.Action);
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        Assert.Equal("no-space-access", Reason(row));
    }

    [Fact]
    public async Task PageSubtree_OfAProtectedNode_IsEmpty()
    {
        var f = await GetFixtureAsync();

        var (bob, _) = ClientFor(Persona.Bob);
        using var bobResult = await bob.PostGraphQLAsync($$"""
            { pageSubtree(spaceId: "{{f.SpaceId}}", pageId: "{{f.P1}}") { ... on PageTreeNode { id } ... on ProtectedTreeNode { title } } }
            """);
        Assert.Empty(Data(bobResult, "pageSubtree").EnumerateArray());

        // Control: a caller who can view P1 gets P5 beneath it.
        var (alice, _) = ClientFor(Persona.Alice);
        using var aliceResult = await alice.PostGraphQLAsync($$"""
            { pageSubtree(spaceId: "{{f.SpaceId}}", pageId: "{{f.P1}}") { ... on PageTreeNode { id } } }
            """);
        Assert.Equal(f.P5, Assert.Single(Data(aliceResult, "pageSubtree").EnumerateArray()).GetProperty("id").GetGuid());
    }

    // ================================================================== links and parent

    [Fact]
    public async Task LinkTargets_DeniedIsProtected_MissingIsNull_VisibleIsAPage()
    {
        var f = await GetFixtureAsync();
        var (bob, sub) = ClientFor(Persona.Bob);

        using var result = await bob.PostGraphQLAsync($$"""
            { pageAccess(id: "{{f.P0}}") { page { id linkTargets {
                id page { id title } denial { placeholderTitle noSpaceAccess marking { label } reasons { gate ruleId } }
              } } } }
            """);
        var body = result.RootElement.ToString();
        var targets = Data(result, "pageAccess").GetProperty("page").GetProperty("linkTargets").EnumerateArray().ToList();

        // First-occurrence order, distinct: P1 (linked twice) once, then P4, then the
        // missing id.
        Assert.Equal([f.P1, f.P4, f.MissingLinkId], targets.Select(t => t.GetProperty("id").GetGuid()));

        Assert.Equal(JsonValueKind.Null, targets[0].GetProperty("page").ValueKind);
        Assert.Equal("UK SECRET APPLE", targets[0].GetProperty("denial").GetProperty("marking").GetProperty("label").GetString());

        Assert.Equal(JsonValueKind.Null, targets[1].GetProperty("page").ValueKind);
        Assert.Equal("RESTRICTION", Assert.Single(targets[1].GetProperty("denial").GetProperty("reasons").EnumerateArray()).GetProperty("gate").GetString());

        Assert.Equal(JsonValueKind.Null, targets[2].GetProperty("page").ValueKind);
        Assert.Equal(JsonValueKind.Null, targets[2].GetProperty("denial").ValueKind);

        Assert.DoesNotContain(P1Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain(P4Title, body, StringComparison.Ordinal);

        // A visible target is a Page, for a caller who can see it.
        var (alice, _) = ClientFor(Persona.Alice);
        using var aliceResult = await alice.PostGraphQLAsync($$"""
            { pageAccess(id: "{{f.P0}}") { page { linkTargets { id page { id title } denial { placeholderTitle } } } } }
            """);
        var aliceTargets = Data(aliceResult, "pageAccess").GetProperty("page").GetProperty("linkTargets").EnumerateArray().ToList();
        Assert.Equal(P1Title, aliceTargets[0].GetProperty("page").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, aliceTargets[0].GetProperty("denial").ValueKind);

        // No per-target audit rows (§7): the read of P0 is the audited action.
        Assert.Empty(await AuditRowsAsync(sub, f.P1));
        Assert.Empty(await AuditRowsAsync(sub, f.P4));
        var p0Row = Assert.Single(await AuditRowsAsync(sub, f.P0));
        Assert.Equal(AuditOutcome.Success, p0Row.Outcome);
    }

    [Fact]
    public async Task ParentDenial_ForAChildUnderAProtectedParent()
    {
        // Markings do not accumulate (§21.5): Bob reads P5 (UK OFFICIAL) under P1
        // (UK SECRET APPLE). `parent` stays null; `parentDenial` is the placeholder.
        var f = await GetFixtureAsync();
        var (bob, sub) = ClientFor(Persona.Bob);

        using var result = await bob.PostGraphQLAsync($$"""
            { pageAccess(id: "{{f.P5}}") { page { id parent { id } parentDenial { placeholderTitle noSpaceAccess marking { label } reasons { gate } } } } }
            """);
        var page = Data(result, "pageAccess").GetProperty("page");
        Assert.Equal(f.P5, page.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("parent").ValueKind);
        var parentDenial = page.GetProperty("parentDenial");
        Assert.Equal("(protected)", parentDenial.GetProperty("placeholderTitle").GetString());
        Assert.Equal("UK SECRET APPLE", parentDenial.GetProperty("marking").GetProperty("label").GetString());
        Assert.DoesNotContain(f.P1.ToString(), result.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);

        // The parent is a directly requested subject: one Denied row, however many of
        // the two fields asked.
        var row = Assert.Single(await AuditRowsAsync(sub, f.P1));
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        Assert.Equal("selector:not_granted:FRUIT", Reason(row));

        // Control: no parentDenial where the parent is viewable or absent.
        var (alice, _) = ClientFor(Persona.Alice);
        using var aliceResult = await alice.PostGraphQLAsync($$"""
            { child: pageAccess(id: "{{f.P5}}") { page { parent { id } parentDenial { placeholderTitle } } }
              root: pageAccess(id: "{{f.P0}}") { page { parent { id } parentDenial { placeholderTitle } } } }
            """);
        Assert.Equal(f.P1, Data(aliceResult, "child").GetProperty("page").GetProperty("parent").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, Data(aliceResult, "child").GetProperty("page").GetProperty("parentDenial").ValueKind);
        Assert.Equal(JsonValueKind.Null, Data(aliceResult, "root").GetProperty("page").GetProperty("parentDenial").ValueKind);
    }

    // ================================================================== the omitting surfaces

    [Theory]
    [InlineData("search")]
    [InlineData("pageQuery")]
    [InlineData("labels")]
    [InlineData("activityFeed")]
    [InlineData("myStaleContent")]
    [InlineData("myRecentlyViewed")]
    [InlineData("notifications")]
    [InlineData("children")]
    [InlineData("askWiki")]
    [InlineData("mcp:search")]
    [InlineData("mcp:get_page")]
    [InlineData("mcp:list_spaces")]
    [InlineData("mcp:get_page_tree")]
    public async Task OmittingSurfaces_CarryNoPlaceholderVocabulary(string surface)
    {
        // §21.8: every surface that is not one of the four disclosing ones keeps
        // omitting - no placeholder, no denial shape, and none of the denied pages'
        // ids or titles - while still returning what Bob may see (the non-vacuity
        // check per surface).
        var f = await GetFixtureAsync();
        var (bob, _) = ClientFor(Persona.Bob);

        string body;
        switch (surface)
        {
            case "search":
                body = await Gql(bob, """query { search(query: "orbital") { totalCount edges { node { snippet page { id title } } } } }""");
                Assert.Contains(f.P0.ToString(), body, StringComparison.OrdinalIgnoreCase);
                break;
            case "pageQuery":
                body = await Gql(bob, $$"""query { pageQuery(query: "space = {{f.SpaceKey}}") { totalCount edges { node { page { id title } } } } }""");
                Assert.Contains(f.P0.ToString(), body, StringComparison.OrdinalIgnoreCase);
                break;
            case "labels":
                body = await Gql(bob, $$"""query { labels(spaceKey: "{{f.SpaceKey}}") labelDetails(spaceKey: "{{f.SpaceKey}}") { id name } }""");
                break;
            case "activityFeed":
                body = await Gql(bob, """query { activityFeed(first: 20) { totalCount nodes { page { id title } } } }""");
                Assert.Contains(f.P0.ToString(), body, StringComparison.OrdinalIgnoreCase);
                break;
            case "myStaleContent":
                body = await Gql(bob, """query { myStaleContent(first: 20) { totalCount nodes { page { id title } } } }""");
                break;
            case "myRecentlyViewed":
                await bob.PostGraphQLAsync($$"""{ page(id: "{{f.P0}}") { id } }""");
                body = await Gql(bob, """query { myRecentlyViewed(first: 20) { nodes { page { id title } } } }""");
                Assert.Contains(f.P0.ToString(), body, StringComparison.OrdinalIgnoreCase);
                break;
            case "notifications":
                body = await Gql(bob, """query { notifications { id type pageId pageTitle } }""");
                break;
            case "children":
                body = await Gql(bob, $$"""query { page(id: "{{f.P0}}") { id children { id title } } }""");
                Assert.Contains("\"children\":[]", body, StringComparison.Ordinal);
                break;
            case "askWiki":
                fixture.ChatClient.Reset();
                body = await Gql(bob, """query { askWiki(question: "orbital") { answer unavailable aggregateMarking { label } citations { pageId title marking { label } } } }""");
                Assert.Contains("\"answer\":\"", body, StringComparison.Ordinal);
                Assert.DoesNotContain(P1Title, fixture.ChatClient.Transcript, StringComparison.Ordinal);
                break;
            case "mcp:search":
                body = await McpAsync(bob, "search", "{\"query\":\"orbital\"}");
                Assert.Contains(f.P0.ToString(), body, StringComparison.OrdinalIgnoreCase);
                break;
            case "mcp:get_page":
                body = await McpAsync(bob, "get_page", "{\"pageId\":\"" + f.P1 + "\"}");
                Assert.Contains(WikiMcpTools.PageNotFoundMessage, body, StringComparison.Ordinal);
                break;
            case "mcp:list_spaces":
                body = await McpAsync(bob, "list_spaces", "{}");
                Assert.Contains(f.SpaceKey, body, StringComparison.Ordinal);
                break;
            case "mcp:get_page_tree":
                body = await McpAsync(bob, "get_page_tree", "{\"spaceKey\":\"" + f.SpaceKey + "\"}");
                Assert.Contains(f.P0.ToString(), body, StringComparison.OrdinalIgnoreCase);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface), surface, null);
        }

        Assert.DoesNotContain(AccessDenialView.ProtectedTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain("noSpaceAccess", body, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholderTitle", body, StringComparison.Ordinal);
        // The ids as JSON string values - a page, a hit, a node. P0's own readable
        // content mentions them inside its page:// links, which is the caller's to read
        // and not what this sweep is about.
        Assert.DoesNotContain(JsonSerializer.Serialize(f.P1.ToString()), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(JsonSerializer.Serialize(f.P4.ToString()), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(P1Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain(P4Title, body, StringComparison.Ordinal);

        static async Task<string> Gql(HttpClient client, string query)
        {
            var (status, body) = await RawGraphQLAsync(client, query);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("\"errors\"", body, StringComparison.Ordinal);
            return body;
        }
    }

    // ================================================================== truth table

    [Theory]
    [InlineData(Persona.Alice, "P0", "")]
    [InlineData(Persona.Alice, "P1", "")]
    [InlineData(Persona.Alice, "P2", "")]
    [InlineData(Persona.Alice, "P3", "NATIONAL_CAVEAT")]
    [InlineData(Persona.Alice, "P4", "RESTRICTION")]
    [InlineData(Persona.Alice, "P5", "")]
    [InlineData(Persona.Alice, "P6", "SELECTOR_GRANT,NATIONAL_CAVEAT")]
    [InlineData(Persona.Bob, "P0", "")]
    [InlineData(Persona.Bob, "P1", "SELECTOR_GRANT")]
    [InlineData(Persona.Bob, "P2", "SELECTOR_GRANT")]
    [InlineData(Persona.Bob, "P3", "")]
    [InlineData(Persona.Bob, "P4", "RESTRICTION")]
    [InlineData(Persona.Bob, "P5", "")]
    [InlineData(Persona.Bob, "P6", "SELECTOR_GRANT,SELECTOR_GRANT")]
    [InlineData(Persona.Carol, "P0", "")]
    [InlineData(Persona.Carol, "P1", "")]
    [InlineData(Persona.Carol, "P2", "")]
    [InlineData(Persona.Carol, "P3", "NATIONAL_CAVEAT")]
    [InlineData(Persona.Carol, "P4", "RESTRICTION")]
    [InlineData(Persona.Carol, "P5", "")]
    [InlineData(Persona.Carol, "P6", "SELECTOR_GRANT,NATIONAL_CAVEAT")]
    [InlineData(Persona.Dave, "P0", "SPACE_ACCESS")]
    [InlineData(Persona.Dave, "P1", "SPACE_ACCESS")]
    [InlineData(Persona.Dave, "P2", "SPACE_ACCESS")]
    [InlineData(Persona.Dave, "P3", "SPACE_ACCESS")]
    [InlineData(Persona.Dave, "P4", "SPACE_ACCESS")]
    [InlineData(Persona.Dave, "P5", "SPACE_ACCESS")]
    [InlineData(Persona.Dave, "P6", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P0", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P1", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P2", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P3", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P4", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P5", "SPACE_ACCESS")]
    [InlineData(Persona.Erin, "P6", "SPACE_ACCESS")]
    public async Task TruthTable(Persona persona, string pageKey, string expectedFailingGates)
    {
        // persona × page: the expected verdict and the exact failing gates on pageAccess,
        // and MCP get_page - an omitting surface over the same ladder - agreeing on the
        // verdict (found ⇔ canView).
        var f = await GetFixtureAsync();
        var pageId = f.Page(pageKey);
        var (client, _) = ClientFor(persona);
        var expected = expectedFailingGates.Length == 0 ? [] : expectedFailingGates.Split(',');

        using var result = await PageAccessAsync(client, pageId);
        var access = Data(result, "pageAccess");
        if (expected.Length == 0)
        {
            Assert.Equal(pageId, access.GetProperty("page").GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, access.GetProperty("denial").ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, access.GetProperty("page").ValueKind);
            Assert.Equal(
                expected,
                access.GetProperty("denial").GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("gate").GetString()));
        }

        var mcp = await McpAsync(client, "get_page", "{\"pageId\":\"" + pageId + "\"}");
        if (expected.Length == 0)
        {
            Assert.Contains(pageId.ToString(), mcp, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(WikiMcpTools.PageNotFoundMessage, mcp, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(WikiMcpTools.PageNotFoundMessage, mcp, StringComparison.Ordinal);
            Assert.DoesNotContain(pageId.ToString(), mcp, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ================================================================== grants

    [Fact]
    public async Task CreateSpace_RequiresASpaceAdminRoleGrant()
    {
        var (admin, _) = ClientFor(Persona.Admin);
        var key = $"NOA{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        using var result = await admin.PostGraphQLAsync($$"""
            mutation { createSpace(
                input: { key: "{{key}}", name: "No admin", description: null }
                initialGrants: [{ kind: ACCESS_GRANT, expressionJson: {{Expr(new EveryoneCondition())}} }]
              ) { space { id } error { kind message } } }
            """);

        var payload = Data(result, "createSpace");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("space").ValueKind);
        Assert.Equal("Validation", payload.GetProperty("error").GetProperty("kind").GetString());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.Spaces.AnyAsync(s => s.Key == key));
    }

    [Fact]
    public async Task CreateAccessRule_AccessGrantWithSelectorValues_RoundTrips_AndIsManagerOnly()
    {
        // A space whose managers are a group Bob is not in (the fixture space makes
        // everyone a Space-admin, so it cannot show the gate).
        var (admin, _) = ClientFor(Persona.Admin);
        var key = $"MGR{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var spaceId = await CreateSpaceAsync(admin, key, string.Join(", ",
            $$"""{ kind: ROLE_GRANT, role: SPACE_ADMIN, expressionJson: {{Expr(new GroupCondition("mgrs"))}} }""",
            $$"""{ kind: ACCESS_GRANT, expressionJson: {{Expr(new EveryoneCondition())}} }"""));

        var created = await CreateAccessRuleAsync(
            admin, "ACCESS_GRANT", spaceId, null, null, null, new GroupCondition("banana-readers"), [("fruit", "banana")]);
        AssertNoError(created);
        var rule = created.GetProperty("rule");
        Assert.Equal("ACCESS_GRANT", rule.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("role").ValueKind);
        var value = Assert.Single(rule.GetProperty("selectorValues").EnumerateArray());
        // Canonicalized on the way in (§21.15).
        Assert.Equal(("FRUIT", "BANANA"), (value.GetProperty("category").GetString(), value.GetProperty("value").GetString()));

        using var listed = await admin.PostGraphQLAsync($$"""
            { space(key: "{{key}}") { canManageAccess grants { id kind role selectorValues { category value } } } }
            """);
        var space = Data(listed, "space");
        Assert.True(space.GetProperty("canManageAccess").GetBoolean());
        var listedRule = space.GetProperty("grants").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == rule.GetProperty("id").GetGuid());
        Assert.Equal("BANANA", Assert.Single(listedRule.GetProperty("selectorValues").EnumerateArray()).GetProperty("value").GetString());

        // Bob sees the space (everyone has access) but manages nothing in it.
        var (bob, _) = ClientFor(Persona.Bob);
        var refused = await CreateAccessRuleAsync(
            bob, "ACCESS_GRANT", spaceId, null, null, null, new GroupCondition("bobs-friends"), [("FRUIT", "APPLE")]);
        Assert.Equal("Forbidden", refused.GetProperty("error").GetProperty("kind").GetString());

        using var bobView = await bob.PostGraphQLAsync($$"""{ space(key: "{{key}}") { viewerHasAccess canManageAccess grants { id } } }""");
        Assert.True(Data(bobView, "space").GetProperty("viewerHasAccess").GetBoolean());
        Assert.False(Data(bobView, "space").GetProperty("canManageAccess").GetBoolean());
        Assert.Empty(Data(bobView, "space").GetProperty("grants").EnumerateArray());
    }

    [Fact]
    public async Task CreateAccessRule_RoleGrantWithSelectorValues_IsAValidationError()
    {
        var f = await GetFixtureAsync();
        var (admin, _) = ClientFor(Persona.Admin);

        var result = await CreateAccessRuleAsync(
            admin, "ROLE_GRANT", f.SpaceId, null, "EDITOR", null, new GroupCondition("confused"), [("FRUIT", "APPLE")]);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("rule").ValueKind);
        Assert.Equal("Validation", result.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Space_ViewerSelectorGrants_IsTheUnionOverMatchedAccessGrants()
    {
        var f = await GetFixtureAsync();

        static async Task<(bool HasAccess, List<(string, string)> Grants)> ReadAsync(HttpClient client, string key)
        {
            using var result = await client.PostGraphQLAsync($$"""{ space(key: "{{key}}") { viewerHasAccess viewerSelectorGrants { category value } } }""");
            var space = Data(result, "space");
            return (
                space.GetProperty("viewerHasAccess").GetBoolean(),
                space.GetProperty("viewerSelectorGrants").EnumerateArray()
                    .Select(v => (v.GetProperty("category").GetString()!, v.GetProperty("value").GetString()!)).ToList());
        }

        // The admin matches readers, apple-readers and north-readers: the union, in
        // canonical order.
        var (admin, _) = ClientFor(Persona.Admin);
        var adminFacts = await ReadAsync(admin, f.SpaceKey);
        Assert.True(adminFacts.HasAccess);
        Assert.Equal([("FRUIT", "APPLE"), ("REGION", "NORTH")], adminFacts.Grants);

        var (alice, _) = ClientFor(Persona.Alice);
        var aliceFacts = await ReadAsync(alice, f.SpaceKey);
        Assert.True(aliceFacts.HasAccess);
        Assert.Equal([("FRUIT", "APPLE")], aliceFacts.Grants);

        // Access with no selector: the ordinary case.
        var (bob, _) = ClientFor(Persona.Bob);
        var bobFacts = await ReadAsync(bob, f.SpaceKey);
        Assert.True(bobFacts.HasAccess);
        Assert.Empty(bobFacts.Grants);

        // A role grant confers nothing here.
        var (erin, _) = ClientFor(Persona.Erin);
        var erinFacts = await ReadAsync(erin, f.SpaceKey);
        Assert.False(erinFacts.HasAccess);
        Assert.Empty(erinFacts.Grants);
    }

    [Fact]
    public async Task Space_CanManageAccess_TrueForRoleOnlyAdmin_WithoutAccess_AndViewerHasAccessFalse()
    {
        // §6.5.2: managing without reading is a normal state. Dave is a Space-admin
        // through the everyone role grant and matches no access grant.
        var f = await GetFixtureAsync();
        var (dave, _) = ClientFor(Persona.Dave);

        using var result = await dave.PostGraphQLAsync($$"""
            { space(key: "{{f.SpaceKey}}") { canManageAccess viewerHasAccess viewerSelectorGrants { category } grants { id } } }
            """);
        var space = Data(result, "space");
        Assert.True(space.GetProperty("canManageAccess").GetBoolean());
        Assert.False(space.GetProperty("viewerHasAccess").GetBoolean());
        Assert.Empty(space.GetProperty("viewerSelectorGrants").EnumerateArray());
        // A manager reads the grants they administer, access or no access.
        Assert.NotEmpty(space.GetProperty("grants").EnumerateArray());
    }

    // ================================================================== markings

    [Fact]
    public async Task SetPageMarking_TwoValuesInOneCategory_IsValidation()
    {
        var f = await GetFixtureAsync();
        var page = await CreateScratchPageAsync(f);
        var (admin, _) = ClientFor(Persona.Admin);

        var payload = await SetMarkingAsync(admin, page, "OFFICIAL", [], [("FRUIT", "APPLE"), ("FRUIT", "BANANA")]);

        Assert.Equal("Validation", payload.GetProperty("error").GetProperty("kind").GetString());
        Assert.Contains("FRUIT", payload.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SetPageMarking_UnknownSelectorValue_IsValidation()
    {
        var f = await GetFixtureAsync();
        var page = await CreateScratchPageAsync(f);
        var (admin, _) = ClientFor(Persona.Admin);

        var unknownValue = await SetMarkingAsync(admin, page, "OFFICIAL", [], [("FRUIT", "CHERRY")]);
        Assert.Equal("Validation", unknownValue.GetProperty("error").GetProperty("kind").GetString());

        var unknownCategory = await SetMarkingAsync(admin, page, "OFFICIAL", [], [("VEGETABLE", "CARROT")]);
        Assert.Equal("Validation", unknownCategory.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task SetPageMarking_CaveatOutsideTheFixedSet_IsRejectedByTheEnum()
    {
        // §21.4: the caveat vocabulary is the input enum, so a token outside the five -
        // and a bare string - fails schema validation before any resolver runs.
        var f = await GetFixtureAsync();
        var page = await CreateScratchPageAsync(f);
        var (admin, _) = ClientFor(Persona.Admin);

        foreach (var literal in new[] { "ZZ", "GB", "\"NZ\"" })
        {
            var (status, body) = await RawGraphQLAsync(admin, $$"""
                mutation { setPageMarking(input: { pageId: "{{page}}", level: OFFICIAL, eyesOnly: [{{literal}}], selectors: [], ukPrefix: true }) { marking { label } error { kind } } }
                """);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("\"errors\"", body, StringComparison.Ordinal);
        }

        using var unchanged = await admin.PostGraphQLAsync($$"""{ page(id: "{{page}}") { marking { label } } }""");
        Assert.Equal("UK OFFICIAL", Data(unchanged, "page").GetProperty("marking").GetProperty("label").GetString());

        // ... and every member of the enum is accepted.
        var accepted = await SetMarkingAsync(admin, page, "OFFICIAL", ["AUS", "CAN", "NZ", "UK", "US"], []);
        Assert.Equal("UK OFFICIAL AUS/CAN/NZ/UK/US EYES ONLY", accepted.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task SetPageMarking_ASelectorYouAreNotGrantedHere_IsForbidden()
    {
        // §21.6's self-lockout rule through the selector gate: Alice may edit, but no
        // access grant she matches confers NORTH - so the marking she would set is one
        // she could not then read.
        var f = await GetFixtureAsync();
        var page = await CreateScratchPageAsync(f);
        var (alice, _) = ClientFor(Persona.Alice);

        var payload = await SetMarkingAsync(alice, page, "OFFICIAL", [], [("REGION", "NORTH")]);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("marking").ValueKind);
        Assert.Equal("Forbidden", payload.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("selector:not_granted:REGION", payload.GetProperty("error").GetProperty("message").GetString());

        // The value she IS granted goes through.
        var allowed = await SetMarkingAsync(alice, page, "OFFICIAL", [], [("FRUIT", "APPLE")]);
        Assert.Equal("UK OFFICIAL APPLE", allowed.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task SetPageMarking_UkPrefixFalse_RendersBareLabel()
    {
        var f = await GetFixtureAsync();
        var page = await CreateScratchPageAsync(f);
        var (admin, _) = ClientFor(Persona.Admin);

        var bare = await SetMarkingAsync(admin, page, "OFFICIAL_SENSITIVE", ["NZ"], [("FRUIT", "APPLE")], ukPrefix: false);
        Assert.Equal("OFFICIAL-SENSITIVE APPLE NZ EYES ONLY", bare.GetProperty("marking").GetProperty("label").GetString());

        var prefixed = await SetMarkingAsync(admin, page, "OFFICIAL_SENSITIVE", ["NZ"], [("FRUIT", "APPLE")], ukPrefix: true);
        Assert.Equal("UK OFFICIAL-SENSITIVE APPLE NZ EYES ONLY", prefixed.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Label_IsTheOneServerFormat_AcrossPageTreeSearchMcpAndAggregate()
    {
        // §21.1: one formatter. The admin can read P6, so its label reaches every
        // surface, and every surface renders the identical string.
        var f = await GetFixtureAsync();
        var (admin, _) = ClientFor(Persona.Admin);

        using var page = await PageAccessAsync(admin, f.P6);
        Assert.Equal(f.P6, Data(page, "pageAccess").GetProperty("page").GetProperty("id").GetGuid());
        using var marking = await admin.PostGraphQLAsync($$"""{ page(id: "{{f.P6}}") { marking { label selectors { category value } ukPrefix eyesOnly } } }""");
        var view = Data(marking, "page").GetProperty("marking");
        Assert.Equal(P6Label, view.GetProperty("label").GetString());
        Assert.True(view.GetProperty("ukPrefix").GetBoolean());
        Assert.Equal(["NZ", "US"], view.GetProperty("eyesOnly").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            [("FRUIT", "APPLE"), ("REGION", "NORTH")],
            view.GetProperty("selectors").EnumerateArray().Select(s => (s.GetProperty("category").GetString(), s.GetProperty("value").GetString())));

        using var tree = await admin.PostGraphQLAsync($$"""{ pageTree(spaceId: "{{f.SpaceId}}") { ... on PageTreeNode { id marking { label } } } }""");
        var node = Data(tree, "pageTree").EnumerateArray().Single(n => n.TryGetProperty("id", out var id) && id.GetGuid() == f.P6);
        Assert.Equal(P6Label, node.GetProperty("marking").GetProperty("label").GetString());

        using var search = await admin.PostGraphQLAsync("""{ search(query: "hexafluoride") { aggregateMarking { label } edges { node { page { id marking { label } } } } } }""");
        var hit = Assert.Single(Data(search, "search").GetProperty("edges").EnumerateArray());
        Assert.Equal(P6Label, hit.GetProperty("node").GetProperty("page").GetProperty("marking").GetProperty("label").GetString());
        Assert.Equal(P6Label, Data(search, "search").GetProperty("aggregateMarking").GetProperty("label").GetString());

        // The tool result rides inside a JSON-encoded text block, so the label's own
        // quotes are escaped there; the string itself is what must match.
        var mcp = await McpAsync(admin, "get_page", "{\"pageId\":\"" + f.P6 + "\"}");
        Assert.Contains(P6Label, mcp, StringComparison.Ordinal);

        // And the placeholder a denied reader sees carries the same string.
        var (bob, _) = ClientFor(Persona.Bob);
        using var denied = await PageAccessAsync(bob, f.P6);
        Assert.Equal(P6Label, Data(denied, "pageAccess").GetProperty("denial").GetProperty("marking").GetProperty("label").GetString());
    }

    // ================================================================== vocabulary

    [Fact]
    public async Task SelectorCategories_AreNameDescriptionAndValues_AndEmptyForAnonymous()
    {
        // The type used to carry `requiresAttribute` - whether a Keycloak claim gated
        // eligibility for the category - and a test that the claim NAME never leaked.
        // Both concepts are gone with the eligibility gate: a category is its name, its
        // description and its values, and nothing about it says who may use it, because
        // that is the space's grant (Space.viewerSelectorGrants).
        var client = Factory.CreateClient();
        client.SetTestUser($"vocab-{Guid.NewGuid():N}");

        using var result = await client.PostGraphQLAsync("""
            { selectorCategories { name description values }
              type: __type(name: "SelectorCategory") { fields { name } } }
            """);
        var body = result.RootElement.ToString();

        var categories = Data(result, "selectorCategories").EnumerateArray().ToList();
        Assert.Equal(["FRUIT", "REGION", RocketWikiApiFactory.SentinelSelectorCategory], categories.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(["APPLE", "BANANA"], categories[0].GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["NORTH", "SOUTH"], categories[1].GetProperty("values").EnumerateArray().Select(v => v.GetString()));

        Assert.Equal(
            new HashSet<string> { "name", "description", "values" },
            Data(result, "type").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToHashSet());
        Assert.DoesNotContain("claimName", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requiresAttribute", body, StringComparison.OrdinalIgnoreCase);

        var anonymous = Factory.CreateClient();
        anonymous.ClearTestUser();
        using var empty = await anonymous.PostGraphQLAsync("{ selectorCategories { name } }");
        Assert.Empty(Data(empty, "selectorCategories").EnumerateArray());
    }
}
