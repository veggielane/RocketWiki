using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// End-to-end coverage of the one real resolver that exists today, over the
/// real ASP.NET Core + Hot Chocolate pipeline (design.md §14's SQLite tier).
/// Deliberately thin — the point right now is proving the fixture (fake auth,
/// SQLite-backed DbContext, FileSystemFileStorage) actually works, so future
/// resolvers landing in RocketWiki.Core/Services have somewhere to add real
/// coverage rather than inventing their own harness.
/// </summary>
public sealed class MeQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task Me_WithNoTestUser_ReturnsAnonymous()
    {
        var client = factory.CreateClient();

        var result = await client.PostGraphQLAsync("{ me { isAuthenticated id groups } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, me.GetProperty("id").ValueKind);
        Assert.Empty(me.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Me_WithTestUser_ReturnsClaimsDerivedIdentity()
    {
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: "user-123",
            email: "alice.engineer@example.test",
            name: "Alice Engineer",
            groups: ["engineering"],
            nationality: ["NZ"]);

        var result = await client.PostGraphQLAsync("{ me { id email name isAuthenticated groups } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.Equal("user-123", me.GetProperty("id").GetString());
        Assert.Equal("alice.engineer@example.test", me.GetProperty("email").GetString());
        Assert.Equal("Alice Engineer", me.GetProperty("name").GetString());
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(["engineering"], me.GetProperty("groups").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_WithDualNationalTestUser_ReturnsBothNationalities()
    {
        // Renamed from ..._DoesNotLeakNationalityIntoMeQuery, whose premise expired:
        // nationality IS part of CurrentUser since §21's marking affordance needed it.
        // Echoing the caller's own token back to them is the same category as `groups`
        // — it is not a leak, and a test asserting otherwise by its NAME would be a
        // standing lie in a security-adjacent file. Authorization still happens
        // server-side; this is affordance data, never a decision (design.md §6.1).
        var client = factory.CreateClient();
        client.SetTestUser(sub: "user-dual", groups: ["engineering"], nationality: ["NZ", "UK"]);

        var result = await client.PostGraphQLAsync("{ me { id isAuthenticated nationality } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.Equal("user-dual", me.GetProperty("id").GetString());
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(["NZ", "UK"], me.GetProperty("nationality").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("  Uk  ")]
    [InlineData("UK")]
    public async Task Me_Nationality_IsCanonicalized_SoTheUiComparesLikeTheServerDoes(string claimValue)
    {
        // design.md §21.4's case-mismatch trap, one layer up. A marking's country set is
        // ALWAYS canonical (upper-cased) on write, and CaveatGate canonicalizes the
        // principal's side before comparing — so the server admits a `gb` token to a `GB`
        // marking. If `me.nationality` echoed the raw claim, the SPA comparing it against
        // `marking.eyesOnly` would disagree with the server and warn that a marking locks
        // you out when it does not. That is the exact property this field exists to
        // provide, so it is pinned here rather than left to the resolver's good manners.
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"user-{Guid.NewGuid()}", nationality: [claimValue]);

        var result = await client.PostGraphQLAsync("{ me { nationality } }");

        Assert.Equal(
            ["UK"],
            result.RootElement.GetProperty("data").GetProperty("me")
                .GetProperty("nationality").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_Nationality_DropsTokensOutsideTheFixedSet_SoTheUiSeesWhatTheGateHolds()
    {
        // The principal side of §21.4: a mapper emitting GB yields a caller who holds
        // NOTHING - and me says so, which is how the misconfiguration gets noticed
        // rather than half-worked-around by a UI comparing raw strings.
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"user-{Guid.NewGuid()}", nationality: ["GB", "FR"]);

        var result = await client.PostGraphQLAsync("{ me { nationality } }");

        Assert.Empty(result.RootElement.GetProperty("data").GetProperty("me").GetProperty("nationality").EnumerateArray());
    }

    [Fact]
    public async Task Me_CarriesNoClearanceAndNoSelectorEligibility()
    {
        // Two affordance fields used to ride here for two gates this deployment no
        // longer has (a clearance against the level, a per-category eligibility claim).
        // Both are gone from the schema, so a document selecting them is refused before
        // any resolver runs - which is what keeps a stale SPA from rendering a picker
        // greyed out by a fact the server no longer holds.
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"user-{Guid.NewGuid()}", nationality: ["UK"]);

        foreach (var field in new[] { "clearance", "selectorEligibility" })
        {
            var response = await client.PostAsync(
                "/graphql",
                new StringContent($$"""{"query":"{ me { {{field}} } }"}""", System.Text.Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("\"errors\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Me_Anonymous_ReportsNoNationality()
    {
        // Anonymous holds nothing: every caveated page is closed to them, and an
        // unauthenticated SPA renders an empty caveat affordance rather than a crash.
        var client = factory.CreateClient();
        client.ClearTestUser();

        var result = await client.PostGraphQLAsync("{ me { isAuthenticated nationality } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Empty(me.GetProperty("nationality").EnumerateArray());
    }
}
