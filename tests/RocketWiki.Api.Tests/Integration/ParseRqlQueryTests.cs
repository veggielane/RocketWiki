using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The <c>parseRql</c> root field (design.md §22): the AST as GraphQL <b>output</b>, the
/// canonical printed form, and positioned errors — for a structural query builder that
/// renders the shape of a query without owning a parser.
///
/// <para>Two properties are asserted here rather than assumed. It <b>executes nothing</b>:
/// no page, space or user is touched, which is what makes it safe with no permission gate
/// beyond authentication. And it <b>reveals no existence</b>: a real space key, an invisible
/// one, and one that never existed parse to structurally identical results (design.md
/// §6.7).</para>
/// </summary>
public sealed class ParseRqlQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string Selection =
        "isValid canonical errors { code message offset length } orderBy { field direction } "
        + "where { kind offset length field operator values { kind text nowOffset } "
        + "children { kind field operator values { kind text nowOffset } "
        + "children { kind field operator values { kind text nowOffset } } } }";

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"parse-{Guid.NewGuid():N}");
        return client;
    }

    private async Task<JsonElement> ParseAsync(HttpClient client, string rql)
    {
        var response = await client.PostGraphQLAsync($$"""
            query { parseRql(query: "{{rql}}") { {{Selection}} } }
            """);
        return response.RootElement.GetProperty("data").GetProperty("parseRql").Clone();
    }

    [Fact]
    public async Task ParseRql_ReturnsTheTreeAndTheCanonicalForm()
    {
        var result = await ParseAsync(
            CreateClient(), """space = ENG AND (label = howto OR label = reference) ORDER BY updated DESC""");

        Assert.True(result.GetProperty("isValid").GetBoolean());
        Assert.Equal(
            """space = "ENG" AND (label = "howto" OR label = "reference") ORDER BY updated DESC""",
            result.GetProperty("canonical").GetString());
        Assert.Empty(result.GetProperty("errors").EnumerateArray());

        var order = Assert.Single(result.GetProperty("orderBy").EnumerateArray());
        Assert.Equal("UPDATED", order.GetProperty("field").GetString());
        Assert.Equal("DESCENDING", order.GetProperty("direction").GetString());

        var root = result.GetProperty("where");
        Assert.Equal("AND", root.GetProperty("kind").GetString());
        var children = root.GetProperty("children").EnumerateArray().ToArray();
        Assert.Equal(2, children.Length);

        Assert.Equal("PREDICATE", children[0].GetProperty("kind").GetString());
        Assert.Equal("SPACE", children[0].GetProperty("field").GetString());
        Assert.Equal("EQUALS", children[0].GetProperty("operator").GetString());
        var value = Assert.Single(children[0].GetProperty("values").EnumerateArray());
        Assert.Equal("TEXT", value.GetProperty("kind").GetString());
        Assert.Equal("ENG", value.GetProperty("text").GetString());

        Assert.Equal("OR", children[1].GetProperty("kind").GetString());
        Assert.Equal(2, children[1].GetProperty("children").GetArrayLength());
    }

    [Fact]
    public async Task ParseRql_ExposesFunctionValuesStructurally()
    {
        var currentUser = await ParseAsync(CreateClient(), "creator = currentUser()");
        var currentUserValue = Assert.Single(currentUser.GetProperty("where").GetProperty("values").EnumerateArray());
        Assert.Equal("CURRENT_USER", currentUserValue.GetProperty("kind").GetString());

        var now = await ParseAsync(CreateClient(), """updated > now(\"-7d\")""");
        var nowValue = Assert.Single(now.GetProperty("where").GetProperty("values").EnumerateArray());
        Assert.Equal("NOW", nowValue.GetProperty("kind").GetString());
        Assert.Equal("-7d", nowValue.GetProperty("nowOffset").GetString());

        var date = await ParseAsync(CreateClient(), """created >= \"2026-01-31\" """);
        var dateValue = Assert.Single(date.GetProperty("where").GetProperty("values").EnumerateArray());
        Assert.Equal("DATE", dateValue.GetProperty("kind").GetString());
        Assert.Equal("2026-01-31", dateValue.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ParseRql_ReportsErrorsWithPositions_AndDistinguishesTheThreeRefusals()
    {
        var client = CreateClient();

        var forbidden = await ParseAsync(client, """marking = \"SECRET\" """);
        Assert.False(forbidden.GetProperty("isValid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, forbidden.GetProperty("canonical").ValueKind);
        Assert.Equal(JsonValueKind.Null, forbidden.GetProperty("where").ValueKind);
        var error = Assert.Single(forbidden.GetProperty("errors").EnumerateArray());
        Assert.Equal("NOT_QUERYABLE_FIELD", error.GetProperty("code").GetString());
        Assert.Equal(0, error.GetProperty("offset").GetInt32());
        Assert.Equal(7, error.GetProperty("length").GetInt32());

        var unsupported = await ParseAsync(client, """text ~ \"x\" """);
        Assert.Equal("UNSUPPORTED_FIELD",
            unsupported.GetProperty("errors").EnumerateArray().Single().GetProperty("code").GetString());

        var unknown = await ParseAsync(client, """banana ~ \"x\" """);
        Assert.Equal("UNKNOWN_FIELD",
            unknown.GetProperty("errors").EnumerateArray().Single().GetProperty("code").GetString());

        var syntax = await ParseAsync(client, "space =");
        var syntaxError = Assert.Single(syntax.GetProperty("errors").EnumerateArray());
        Assert.Equal("SYNTAX", syntaxError.GetProperty("code").GetString());
        Assert.Equal(7, syntaxError.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task ParseRql_RevealsNothingAboutWhetherAnythingExists()
    {
        // A real space, an invisible one, and one that never existed - the caller cannot
        // tell which is which from a parse (design.md §6.7).
        var (realKey, hiddenKey) = await SeedRealAndHiddenSpacesAsync();
        var client = CreateClient();

        var real = await ParseAsync(client, $"space = {realKey}");
        var hidden = await ParseAsync(client, $"space = {hiddenKey}");
        var imaginary = await ParseAsync(client, "space = NOSUCHSPACEKEY");

        foreach (var result in new[] { real, hidden, imaginary })
        {
            Assert.True(result.GetProperty("isValid").GetBoolean());
            Assert.Empty(result.GetProperty("errors").EnumerateArray());
            Assert.Equal("SPACE", result.GetProperty("where").GetProperty("field").GetString());
        }

        // The only thing that differs between the three responses is the key the caller
        // typed, echoed back in the canonical form.
        Assert.Equal($"""space = "{realKey}" """.TrimEnd(), real.GetProperty("canonical").GetString());
        Assert.Equal($"""space = "{hiddenKey}" """.TrimEnd(), hidden.GetProperty("canonical").GetString());
    }

    [Fact]
    public async Task ParseRql_WritesNoAuditRow()
    {
        // design.md §7/§22: no subject, no access decision, nothing read - and a
        // live-syntax-checking editor would otherwise write a row per keystroke.
        var marker = $"nau{Guid.NewGuid():N}"[..12];
        _ = await ParseAsync(CreateClient(), $"title ~ {marker}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Empty(db.AuditEvents.Where(e => e.DetailsJson != null && e.DetailsJson.Contains(marker)));
    }

    private async Task<(string RealKey, string HiddenKey)> SeedRealAndHiddenSpacesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"parseseed-{Guid.NewGuid():N}",
            DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);

        string Add(string expressionJson)
        {
            var space = new Space
            {
                Key = $"P{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "Parse Space",
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
                ExpressionJson = expressionJson,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = creator.Id,
            });
            return space.Key;
        }

        var realKey = Add(RuleExpressionSerializer.Serialize(new EveryoneCondition()));
        var hiddenKey = Add(RuleExpressionSerializer.Serialize(new GroupCondition("parse-insiders")));

        await db.SaveChangesAsync();
        return (realKey, hiddenKey);
    }
}
