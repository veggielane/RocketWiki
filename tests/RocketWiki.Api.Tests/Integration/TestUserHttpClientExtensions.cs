using System.Text;
using System.Text.Json;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Friendly wrapper over <see cref="TestAuthHandler"/>'s header encoding, so
/// tests describe a fake principal the way design.md §6.1 describes one
/// (sub/email/name plus groups/nationality/clearance claims, and since §21.15 the
/// selector eligibility claims) instead of building
/// <see cref="System.Security.Claims.Claim"/> lists by hand.
/// </summary>
public static class TestUserHttpClientExtensions
{
    /// <param name="selectorClaims">Selector eligibility claims to answer <c>yes</c>
    /// (design.md §21.15) — each name is emitted as <c>(name, "yes")</c>, the value
    /// <c>SelectorGate</c> admits. <see cref="RocketWikiApiFactory.FruitClaim"/> is the
    /// one the shared test catalog gates on.</param>
    /// <param name="claims">The escape hatch: arbitrary <c>(type, value)</c> claims,
    /// for the cases the named parameters cannot state — a selector claim whose value is
    /// not <c>yes</c>, or a claim nobody configured, both of which the principal builder
    /// must NOT turn into eligibility.</param>
    public static void SetTestUser(
        this HttpClient client,
        string sub,
        string? email = null,
        string? name = null,
        IEnumerable<string>? groups = null,
        IEnumerable<string>? nationality = null,
        IEnumerable<string>? roles = null,
        string? clearance = null,
        IEnumerable<string>? selectorClaims = null,
        IEnumerable<(string Type, string Value)>? claims = null)
    {
        var encoded = BuildEncodedClaimsHeaderValue(
            sub, email, name, groups, nationality, roles, clearance, selectorClaims, claims);

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
        string? clearance = null,
        IEnumerable<string>? selectorClaims = null,
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

        // design.md §21: the protective-marking clearance attribute. Claim type matches
        // the "clearance" protocol mapper in the same realm file. Left ABSENT when null
        // rather than emitted as an empty string — "no clearance claim at all" is exactly
        // the case §21's fail-closed default (OFFICIAL-SENSITIVE and nothing above) is
        // written for, and a blank value would be a different code path.
        if (clearance is not null)
        {
            list.Add(new TestAuthHandler.TestClaim("clearance", clearance));
        }

        // design.md §21.15: one `yes` per selector claim - the dev realm's `fruit` mapper
        // emits exactly this shape.
        foreach (var claimName in selectorClaims ?? [])
        {
            list.Add(new TestAuthHandler.TestClaim(claimName, "yes"));
        }

        foreach (var (type, value) in claims ?? [])
        {
            list.Add(new TestAuthHandler.TestClaim(type, value));
        }

        var json = JsonSerializer.Serialize(list);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
