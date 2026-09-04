using System.Text;
using System.Text.Json;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Friendly wrapper over <see cref="TestAuthHandler"/>'s header encoding, so
/// tests describe a fake principal the way design.md §6.1 describes one
/// (sub/email/name plus groups/nationality claims) instead of building
/// <see cref="System.Security.Claims.Claim"/> lists by hand.
///
/// <para>Two named parameters used to sit beside these: <c>clearance</c> and
/// <c>selectorClaims</c>, feeding the level gate and the per-category eligibility gate.
/// Both gates read Keycloak attributes this deployment does not carry and both are gone;
/// a test that wants a caller refused now gives them no grant for a selector, or a
/// nationality outside the caveat, because those are the facts that gate.</para>
/// </summary>
public static class TestUserHttpClientExtensions
{
    /// <param name="claims">The escape hatch: arbitrary <c>(type, value)</c> claims, for
    /// the cases the named parameters cannot state — a claim nobody configured, which the
    /// principal builder must NOT turn into an attribute.</param>
    public static void SetTestUser(
        this HttpClient client,
        string sub,
        string? email = null,
        string? name = null,
        IEnumerable<string>? groups = null,
        IEnumerable<string>? nationality = null,
        IEnumerable<string>? roles = null,
        IEnumerable<(string Type, string Value)>? claims = null)
    {
        var encoded = BuildEncodedClaimsHeaderValue(sub, email, name, groups, nationality, roles, claims);

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
        IEnumerable<string>? roles = null,
        IEnumerable<(string Type, string Value)>? claims = null)
    {
        var list = new List<TestAuthHandler.TestClaim> { new("sub", sub) };

        if (email is not null)
        {
            list.Add(new TestAuthHandler.TestClaim("email", email));
        }

        if (name is not null)
        {
            list.Add(new TestAuthHandler.TestClaim("name", name));
        }

        foreach (var group in groups ?? [])
        {
            list.Add(new TestAuthHandler.TestClaim("groups", group));
        }

        // Claim type matches the "nationality" protocol mapper's claim.name in
        // src/RocketWiki.AppHost/keycloak/rocketwiki-realm.json.
        foreach (var value in nationality ?? [])
        {
            list.Add(new TestAuthHandler.TestClaim("nationality", value));
        }

        // Claim type matches the "roles" protocol mapper's claim.name in the same realm
        // file (design.md §6.5's instance `admin`/`user` roles) - IInstanceRoleAccessor
        // reads this exact claim type.
        foreach (var role in roles ?? [])
        {
            list.Add(new TestAuthHandler.TestClaim("roles", role));
        }

        foreach (var (type, value) in claims ?? [])
        {
            list.Add(new TestAuthHandler.TestClaim(type, value));
        }

        var json = JsonSerializer.Serialize(list);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
