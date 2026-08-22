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
    public async Task Me_WithDualNationalTestUser_DoesNotLeakNationalityIntoMeQuery()
    {
        // nationality isn't part of CurrentUser today (design.md §6.1: the ABAC
        // Principal, not this claims echo, is what carries attributes) — this
        // just confirms setting it doesn't blow up the fake-auth pipeline, as
        // a resolver that does need it (the future rule engine) will rely on
        // the same TestAuthHandler claim set.
        var client = factory.CreateClient();
        client.SetTestUser(sub: "user-dual", groups: ["engineering"], nationality: ["NZ", "GB"]);

        var result = await client.PostGraphQLAsync("{ me { id isAuthenticated } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.Equal("user-dual", me.GetProperty("id").GetString());
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
    }
}
