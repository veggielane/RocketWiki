using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using RocketWiki.Core.Entities;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The profile page (<c>userProfile</c>, design.md §6.2): any signed-in user may read any
/// user's group memberships as recorded at that user's last request.
///
/// <para>Three things are under test. <b>Reach</b>: an ordinary caller can read another
/// person's profile, and anonymous / unknown-id get null. <b>Truth</b>: what the page
/// shows is exactly the <c>groups</c> claim JIT provisioning mirrored — sorted, never
/// interpreted — and it follows the LAST request, not the first. <b>Bounds</b>: the type
/// carries exactly the decided fields, and nationality/email never reach the wire — the
/// disclosure §6.2 widened is group membership, nothing else.</para>
///
/// <para>The page used to show a clearance and a per-category selector eligibility, both
/// re-derived through the gates; those gates read Keycloak attributes this deployment
/// does not carry, and both went, taking the fields with them. The bounds guard below is
/// what says so in code.</para>
/// </summary>
public sealed class UserProfileQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string NationalitySentinel = "ZZNATIONALITYZZ";

    private static string ProfileQuery(Guid id) =>
        $$"""
        { userProfile(id: "{{id}}") { id displayName hasAvatar isExternal groups } }
        """;

    private sealed record Profile(Guid Id, string DisplayName, bool HasAvatar, bool IsExternal, IReadOnlyList<string> Groups);

    private static Profile? ProfileIn(JsonDocument result)
    {
        if (result.RootElement.TryGetProperty("errors", out var errors))
        {
            Assert.Fail(errors.GetRawText());
        }

        var node = result.RootElement.GetProperty("data").GetProperty("userProfile");
        if (node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new Profile(
            node.GetProperty("id").GetGuid(),
            node.GetProperty("displayName").GetString()!,
            node.GetProperty("hasAvatar").GetBoolean(),
            node.GetProperty("isExternal").GetBoolean(),
            node.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).ToList());
    }

    /// <summary>Signs in as <paramref name="sub"/> with the given claims (a request is a
    /// sign-in here: JIT provisioning runs on every authenticated request) and returns the
    /// local row id the profile is addressed by.</summary>
    private async Task<Guid> SignInAsync(
        string sub,
        string? name = null,
        IEnumerable<string>? groups = null,
        IEnumerable<string>? nationality = null,
        IEnumerable<(string Type, string Value)>? claims = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, name: name, groups: groups, nationality: nationality, claims: claims);
        using var me = await client.PostGraphQLAsync("{ me { localUserId } }");
        return me.RootElement.GetProperty("data").GetProperty("me").GetProperty("localUserId").GetGuid();
    }

    /// <summary>A different, ordinary person: no admin role, no space grants, no groups.
    /// The profile must be readable by exactly this caller.</summary>
    private HttpClient OrdinaryViewer()
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");
        return client;
    }

    private async Task<Profile> ReadProfileAsOrdinaryUserAsync(Guid id)
    {
        using var result = await OrdinaryViewer().PostGraphQLAsync(ProfileQuery(id));
        var profile = ProfileIn(result);
        Assert.NotNull(profile);
        return profile;
    }

    [Fact]
    public async Task UserProfile_IsNullForAnonymous_RatherThanErroring()
    {
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}", groups: ["engineering"]);

        var anonymous = factory.CreateClient();
        using var result = await anonymous.PostGraphQLAsync(ProfileQuery(id));

        Assert.Null(ProfileIn(result));
    }

    [Fact]
    public async Task UserProfile_IsNullForAnUnknownId()
    {
        using var result = await OrdinaryViewer().PostGraphQLAsync(ProfileQuery(Guid.NewGuid()));

        Assert.Null(ProfileIn(result));
    }

    /// <summary>
    /// The feature: user A reads what user B's token carried at B's last request. A holds
    /// no groups and no admin role — the values shown are B's, from B's mirror, not A's
    /// token echoed back.
    /// </summary>
    [Fact]
    public async Task UserProfile_ShowsAnotherUsersRecordedGroups_SortedOrdinally()
    {
        var name = $"Profiled {Guid.NewGuid():N}"[..20];
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}", name: name, groups: ["propulsion", "Avionics", "apple-readers"]);

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal(id, profile.Id);
        Assert.Equal(name, profile.DisplayName);
        Assert.False(profile.HasAvatar);
        Assert.False(profile.IsExternal);
        // Ordinal order: upper-case sorts before lower-case, and the token's order is not kept.
        Assert.Equal(["Avionics", "apple-readers", "propulsion"], profile.Groups);
    }

    [Fact]
    public async Task UserProfile_NoGroupsClaim_ReadsAsNoGroups()
    {
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}");

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Empty(profile.Groups);
    }

    /// <summary>
    /// "Recorded at the last sign-in" means the LAST one: a membership that changed in
    /// Keycloak between two requests shows the newer list, never the first one seen.
    /// </summary>
    [Fact]
    public async Task UserProfile_ReflectsTheLastRequest_NotTheFirst()
    {
        var sub = $"profiled-{Guid.NewGuid()}";
        var id = await SignInAsync(sub, groups: ["engineering", "legal"]);
        var again = await SignInAsync(sub, groups: ["engineering"]);
        Assert.Equal(id, again);

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal(["engineering"], profile.Groups);
    }

    [Fact]
    public async Task UserProfile_ShadowUser_IsExternal_WithNothingRecorded()
    {
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var shadow = new User
            {
                Subject = null,
                DisplayName = $"Shadow {Guid.NewGuid():N}"[..20],
                IsExternal = true,
                AttributesJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(shadow);
            await db.SaveChangesAsync();
            id = shadow.Id;
        }

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.True(profile.IsExternal);
        Assert.Empty(profile.Groups);
    }

    /// <summary>
    /// <b>The agreement pin.</b> For a matrix of token shapes, the profile's groups must
    /// equal the <c>groups</c> claim values the token carried, sorted and de-duplicated -
    /// and nothing that is not a groups claim, however group-shaped, may appear.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClaimMatrix))]
    public async Task Profile_ShowsExactlyTheMirroredGroupsClaim(string label, string[] encodedClaims, string[] expectedGroups)
    {
        var claims = encodedClaims
            .Select(c => c.Split('=', 2))
            .Select(parts => (Type: parts[0], Value: parts[1]))
            .ToList();
        var id = await SignInAsync($"matrix-{label}-{Guid.NewGuid()}", claims: claims);

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal(expectedGroups, profile.Groups);
    }

    public static TheoryData<string, string[], string[]> ClaimMatrix => new()
    {
        { "two-groups", ["groups=engineering", "groups=legal"], ["engineering", "legal"] },
        { "no-claims", [], [] },
        { "duplicate", ["groups=engineering", "groups=engineering"], ["engineering"] },
        { "ordinal-order", ["groups=b", "groups=A", "groups=a"], ["A", "a", "b"] },
        { "not-a-groups-claim", ["group=engineering", "roles=admin", "nationality=NZ"], [] },
        { "mixed", ["groups=engineering", "clearance=SECRET", "fruit=yes"], ["engineering"] },
    };

    /// <summary>
    /// <b>The bounds guard</b>, twin of <c>UserDirectory_RowType_ExposesOnlyIdDisplayNameAndAvatarFlag</c>:
    /// the profile type is exactly the decided field set. Adding nationality, email,
    /// last-seen or a recording timestamp here is a §6.2 decision, not a convenience —
    /// update this set only after that decision is written down. The removed fields
    /// (clearance, clearanceName, clearanceRecorded, selectorEligibility) are pinned
    /// absent the same way: the gates they rendered are gone.
    /// </summary>
    [Fact]
    public void UserProfile_TypeExposesExactlyTheDecidedFields()
    {
        var exposed = typeof(UserProfileView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["DisplayName", "Groups", "HasAvatar", "Id", "IsExternal"], exposed);
    }

    /// <summary>
    /// Non-vacuous leak check: the profiled user's mirror really does hold a nationality
    /// (JIT mirrors it, and the profile query fetches that very column), so the sentinel's
    /// absence from the response is about the projection, not about the data.
    /// </summary>
    [Fact]
    public async Task UserProfile_NeverExposesNationalityOrEmail()
    {
        var email = $"{Guid.NewGuid():N}@profile.example.test";
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: $"profiled-{Guid.NewGuid()}", email: email, nationality: [NationalitySentinel], groups: ["engineering"]);
        using var me = await client.PostGraphQLAsync("{ me { localUserId } }");
        var id = me.RootElement.GetProperty("data").GetProperty("me").GetProperty("localUserId").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var row = await db.Users.FindAsync(id);
            Assert.Contains(NationalitySentinel, row!.AttributesJson, StringComparison.Ordinal);
        }

        using var result = await OrdinaryViewer().PostGraphQLAsync(ProfileQuery(id));

        Assert.NotNull(ProfileIn(result));
        var raw = result.RootElement.GetRawText();
        Assert.DoesNotContain(NationalitySentinel, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(email, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AtUtc", raw, StringComparison.Ordinal);
    }

    /// <summary>The directory beside it is unchanged: still <c>UserRef</c> only. The
    /// census widened by one profile at a time, not by an enumerable list of groups.</summary>
    [Fact]
    public void UserDirectory_RowType_IsStillJustUserRef()
    {
        var directoryField = typeof(Query).GetMethod(nameof(Query.UserDirectory));
        Assert.NotNull(directoryField);
        Assert.Equal(typeof(IQueryable<UserRef>), directoryField!.ReturnType);
    }
}
