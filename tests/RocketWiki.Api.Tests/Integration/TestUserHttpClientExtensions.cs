using System.Text;
using System.Text.Json;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Friendly wrapper over <see cref="TestAuthHandler"/>'s header encoding, so
/// tests describe a fake principal the way design.md §6.1 describes one
/// (sub/email/name plus groups/nationality claims) instead of building
/// <see cref="System.Security.Claims.Claim"/> lists by hand.
/// </summary>
public static class TestUserHttpClientExtensions
{
    public static void SetTestUser(
        this HttpClient client,
        string sub,
        string? email = null,
        string? name = null,
        IEnumerable<string>? groups = null,
        IEnumerable<string>? nationality = null,
        IEnumerable<string>? roles = null)
    {
        var encoded = BuildEncodedClaimsHeaderValue(sub, email, name, groups, nationality, roles);

        client.DefaultRequestHeaders.Remove(TestAuthHandler.ClaimsHeaderName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClaimsHeaderName, encoded);
    }

    /// <summary>Reverts a client to the anonymous state — no token, matching
    /// how a real unauthenticated request would present to the API.</summary>
    public static void ClearTestUser(this HttpClient client) =>
        client.DefaultRequestHeaders.Remove(TestAuthHandler.ClaimsHeaderName);

    /// <summary>
    /// The same claims encoding <see cref="SetTestUser"/> uses, exposed separately for
    /// callers that cannot set an <see cref="HttpClient"/> header directly — a SignalR
    /// <c>HubConnection</c> carries its own <c>Headers</c> collection instead
    /// (<c>NotificationsHubTests</c>).
    /// </summary>
    public static string BuildEncodedClaimsHeaderValue(
        string sub,
        string? email = null,
        string? name = null,
        IEnumerable<string>? groups = null,
        IEnumerable<string>? nationality = null,
        IEnumerable<string>? roles = null)
    {
        var claims = new List<TestAuthHandler.TestClaim> { new("sub", sub) };

        if (email is not null)
        {
            claims.Add(new TestAuthHandler.TestClaim("email", email));
        }

        if (name is not null)
        {
            claims.Add(new TestAuthHandler.TestClaim("name", name));
        }

        foreach (var group in groups ?? [])
        {
            claims.Add(new TestAuthHandler.TestClaim("groups", group));
        }

        // Claim type matches the "nationality" protocol mapper's claim.name in
        // src/RocketWiki.AppHost/keycloak/rocketwiki-realm.json.
        foreach (var value in nationality ?? [])
        {
            claims.Add(new TestAuthHandler.TestClaim("nationality", value));
        }

        // Claim type matches the "roles" protocol mapper's claim.name in the same realm
        // file (design.md §6.5's instance `admin`/`user` roles) - IInstanceRoleAccessor
        // reads this exact claim type.
        foreach (var role in roles ?? [])
        {
            claims.Add(new TestAuthHandler.TestClaim("roles", role));
        }

        var json = JsonSerializer.Serialize(claims);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
