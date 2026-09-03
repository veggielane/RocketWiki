using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The profile page (<c>userProfile</c>, design.md §6.2's 2026-09-03 decision): any
/// signed-in user may read any user's clearance and selector eligibility as recorded at
/// that user's last request.
///
/// <para>Three things are under test. <b>Reach</b>: an ordinary caller can read another
/// person's profile, and anonymous / unknown-id get null. <b>Truth</b>: what the page
/// shows is what the gates would decide from the recorded claims — pinned by
/// <see cref="Profile_AgreesWithTheGate"/>, which runs the real gate functions over the
/// same claims and demands equality, so a second implementation of "eligible" or a
/// friendlier clearance parser cannot creep in. <b>Bounds</b>: the type carries exactly
/// the decided fields, and nationality/email never reach the wire — the disclosure §6.2
/// widened is the clearance and eligibility, nothing else.</para>
/// </summary>
public sealed class UserProfileQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string NationalitySentinel = "ZZNATIONALITYZZ";

    private static string ProfileQuery(Guid id) =>
        $$"""
        { userProfile(id: "{{id}}") {
            id displayName hasAvatar isExternal
            clearance clearanceName clearanceRecorded
            selectorEligibility { category requiresAttribute eligible } } }
        """;

    private sealed record Eligibility(string Category, bool RequiresAttribute, bool Eligible);

    private sealed record Profile(
        Guid Id,
        string DisplayName,
        bool HasAvatar,
        bool IsExternal,
        string Clearance,
        string ClearanceName,
        bool ClearanceRecorded,
        IReadOnlyList<Eligibility> SelectorEligibility);

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
            node.GetProperty("clearance").GetString()!,
            node.GetProperty("clearanceName").GetString()!,
            node.GetProperty("clearanceRecorded").GetBoolean(),
            node.GetProperty("selectorEligibility").EnumerateArray()
                .Select(e => new Eligibility(
                    e.GetProperty("category").GetString()!,
                    e.GetProperty("requiresAttribute").GetBoolean(),
                    e.GetProperty("eligible").GetBoolean()))
                .ToList());
    }

    /// <summary>Signs in as <paramref name="sub"/> with the given claims (a request is a
    /// sign-in here: JIT provisioning runs on every authenticated request) and returns the
    /// local row id the profile is addressed by.</summary>
    private async Task<Guid> SignInAsync(
        string sub,
        string? name = null,
        string? clearance = null,
        IEnumerable<string>? selectorClaims = null,
        IEnumerable<string>? nationality = null,
        IEnumerable<(string Type, string Value)>? claims = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: sub, name: name, clearance: clearance, selectorClaims: selectorClaims,
            nationality: nationality, claims: claims);
        using var me = await client.PostGraphQLAsync("{ me { localUserId } }");
        return me.RootElement.GetProperty("data").GetProperty("me").GetProperty("localUserId").GetGuid();
    }

    /// <summary>A different, ordinary person: no admin role, no space grants, no
    /// clearance claim. The profile must be readable by exactly this caller.</summary>
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

    private SelectorCatalog Catalog => factory.Services.GetRequiredService<SelectorCatalog>();

    [Fact]
    public async Task UserProfile_IsNullForAnonymous_RatherThanErroring()
    {
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}", clearance: "SECRET");

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
    /// The feature: user A reads what user B's claims carried at B's last request. A
    /// holds no clearance claim and no admin role — the values shown are B's, resolved
    /// from B's mirror, not A's token echoed back.
    /// </summary>
    [Fact]
    public async Task UserProfile_ShowsAnotherUsersRecordedClearanceAndEligibility()
    {
        var name = $"Profiled {Guid.NewGuid():N}"[..20];
        var id = await SignInAsync(
            $"profiled-{Guid.NewGuid()}", name: name, clearance: "SECRET", selectorClaims: [RocketWikiApiFactory.FruitClaim]);

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal(id, profile.Id);
        Assert.Equal(name, profile.DisplayName);
        Assert.False(profile.HasAvatar);
        Assert.False(profile.IsExternal);
        Assert.Equal("SECRET", profile.Clearance);
        Assert.Equal("SECRET", profile.ClearanceName);
        Assert.True(profile.ClearanceRecorded);
        Eligibility[] expected =
        [
            new("FRUIT", RequiresAttribute: true, Eligible: true),
            new("REGION", RequiresAttribute: false, Eligible: true),
            new(RocketWikiApiFactory.SentinelSelectorCategory, RequiresAttribute: false, Eligible: true),
        ];
        Assert.Equal(expected, profile.SelectorEligibility);
    }

    [Fact]
    public async Task UserProfile_NoClearanceClaim_ReportsTheFloor_AndSaysItWasNotRecorded()
    {
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}");

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal("OFFICIAL_SENSITIVE", profile.Clearance);
        Assert.Equal("OFFICIAL-SENSITIVE", profile.ClearanceName);
        Assert.False(profile.ClearanceRecorded);
    }

    [Fact]
    public async Task UserProfile_UnrecognisedClearance_ReportsTheFloor_AndSaysItWasNotRecorded()
    {
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}", clearance: "not-a-level");

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal("OFFICIAL_SENSITIVE", profile.Clearance);
        Assert.False(profile.ClearanceRecorded);
    }

    /// <summary>A recorded clearance, even one below the floor, is a recorded fact
    /// (§21.3 honours OFFICIAL rather than rounding it up), so it reads as recorded.</summary>
    [Fact]
    public async Task UserProfile_RecordedOfficial_IsHonouredBelowTheFloor_AndReadsAsRecorded()
    {
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}", clearance: "OFFICIAL");

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal("OFFICIAL", profile.Clearance);
        Assert.True(profile.ClearanceRecorded);
    }

    [Fact]
    public async Task UserProfile_ClaimlessCategory_IsEligibleForEveryone_GatedCategoryIsNot()
    {
        // No selector claim at all.
        var id = await SignInAsync($"profiled-{Guid.NewGuid()}");

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        var byCategory = profile.SelectorEligibility.ToDictionary(e => e.Category);
        Assert.Equal(new Eligibility("FRUIT", RequiresAttribute: true, Eligible: false), byCategory["FRUIT"]);
        Assert.Equal(new Eligibility("REGION", RequiresAttribute: false, Eligible: true), byCategory["REGION"]);
        Assert.Equal(
            new Eligibility(RocketWikiApiFactory.SentinelSelectorCategory, RequiresAttribute: false, Eligible: true),
            byCategory[RocketWikiApiFactory.SentinelSelectorCategory]);
    }

    /// <summary>
    /// "Recorded at the last sign-in" means the LAST one: a clearance that changed in
    /// Keycloak between two requests shows the newer value, never the first one seen.
    /// </summary>
    [Fact]
    public async Task UserProfile_ReflectsTheLastRequest_NotTheFirst()
    {
        var sub = $"profiled-{Guid.NewGuid()}";
        var id = await SignInAsync(sub, clearance: "SECRET", selectorClaims: [RocketWikiApiFactory.FruitClaim]);
        var again = await SignInAsync(sub, clearance: "OFFICIAL");
        Assert.Equal(id, again);

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        Assert.Equal("OFFICIAL", profile.Clearance);
        Assert.False(profile.SelectorEligibility.Single(e => e.Category == "FRUIT").Eligible);
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
        Assert.Equal("OFFICIAL_SENSITIVE", profile.Clearance);
        Assert.False(profile.ClearanceRecorded);
        Assert.False(profile.SelectorEligibility.Single(e => e.Category == "FRUIT").Eligible);
    }

    /// <summary>
    /// <b>The agreement pin.</b> For a matrix of claim sets, the profile's clearance and
    /// eligibility must equal what <see cref="ClearanceGate.ResolveClearance"/> and
    /// <see cref="SelectorGate.ResolveEligibleCategories"/> return over the SAME claims,
    /// mapped by the same <see cref="PrincipalBuilder"/> enforcement uses. This is the
    /// test that fails if the profile ever grows its own reading of the rules: drop the
    /// <c>fruit</c> key in <c>MirroredPrincipal</c> and the first row goes red; resolve
    /// the clearance with <c>Enum.TryParse</c> and the <c>Secret</c> and <c>4</c> rows go
    /// red (§21.3: the gate parses the four wire names and nothing else).
    /// </summary>
    [Theory]
    [MemberData(nameof(ClaimMatrix))]
    public async Task Profile_AgreesWithTheGate(string label, string[] encodedClaims)
    {
        var claims = encodedClaims
            .Select(c => c.Split('=', 2))
            .Select(parts => (Type: parts[0], Value: parts[1]))
            .ToList();
        var sub = $"matrix-{label}-{Guid.NewGuid()}";
        var id = await SignInAsync(sub, claims: claims);

        var profile = await ReadProfileAsOrdinaryUserAsync(id);

        // The expected answers, from the gates over the token-built principal.
        var tokenPrincipal = new PrincipalBuilder(Catalog).Build(
            new ClaimsPrincipal(new ClaimsIdentity(
                claims.Select(c => new Claim(c.Type, c.Value)).Prepend(new Claim("sub", sub)),
                authenticationType: "test")));
        Assert.NotNull(tokenPrincipal);
        var expectedClearance = ClearanceGate.ResolveClearance(tokenPrincipal!);
        var expectedEligible = SelectorGate.ResolveEligibleCategories(tokenPrincipal!, Catalog);

        // The GraphQL enum names ARE the gate's four wire names, so the gate's own parser
        // is the decoder - no second spelling table in the test either.
        Assert.True(ClearanceGate.TryParseLevel(profile.Clearance, out var actualClearance), profile.Clearance);
        Assert.Equal(expectedClearance, actualClearance);
        Assert.Equal(ProtectiveMarking.LevelName(expectedClearance), profile.ClearanceName);

        Assert.Equal(
            Catalog.Categories.Select(c => c.Name),
            profile.SelectorEligibility.Select(e => e.Category));
        Assert.Equal(
            Catalog.Categories.Select(c => c.RequiresClaim),
            profile.SelectorEligibility.Select(e => e.RequiresAttribute));
        Assert.Equal(
            expectedEligible.OrderBy(n => n, StringComparer.Ordinal),
            profile.SelectorEligibility.Where(e => e.Eligible).Select(e => e.Category).OrderBy(n => n, StringComparer.Ordinal));
    }

    public static TheoryData<string, string[]> ClaimMatrix => new()
    {
        { "secret-and-fruit", ["clearance=SECRET", "fruit=yes"] },
        { "no-claims", [] },
        { "csharp-spelling", ["clearance=Secret"] },
        { "numeric", ["clearance=4"] },
        { "lowercase", ["clearance=secret"] },
        { "official-below-floor", ["clearance=OFFICIAL"] },
        { "top-secret-plus-garbage-and-padded-yes", ["clearance=TOP_SECRET", "clearance=garbage", "fruit= Yes "] },
        { "multi-valued-takes-highest", ["clearance=OFFICIAL", "clearance=SECRET"] },
        { "fruit-no", ["fruit=no"] },
        { "fruit-blank", ["fruit="] },
        { "unconfigured-selector-claim", ["vegetable=yes"] },
        { "fruit-yes-among-others", ["fruit=no", "fruit=yes", "nationality=NZ"] },
    };

    /// <summary>
    /// <b>The bounds guard</b>, twin of <c>UserDirectory_RowType_ExposesOnlyIdDisplayNameAndAvatarFlag</c>:
    /// the profile type is exactly the decided field set. Adding nationality, email,
    /// last-seen or a recording timestamp here is a §6.2 decision, not a convenience —
    /// update this set only after that decision is written down.
    /// </summary>
    [Fact]
    public void UserProfile_TypeExposesExactlyTheDecidedFields()
    {
        var exposed = typeof(UserProfileView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Clearance", "ClearanceName", "ClearanceRecorded", "DisplayName",
                "HasAvatar", "Id", "IsExternal", "SelectorEligibility",
            ],
            exposed);
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
            sub: $"profiled-{Guid.NewGuid()}", email: email, nationality: [NationalitySentinel], clearance: "SECRET");
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
    }

    /// <summary>The directory beside it is unchanged: still <c>UserRef</c> only. The
    /// census widened by one profile at a time, not by an enumerable list of clearances.</summary>
    [Fact]
    public void UserDirectory_RowType_IsStillJustUserRef()
    {
        var directoryField = typeof(Query).GetMethod(nameof(Query.UserDirectory));
        Assert.NotNull(directoryField);
        Assert.Equal(typeof(IQueryable<UserRef>), directoryField!.ReturnType);
    }
}
