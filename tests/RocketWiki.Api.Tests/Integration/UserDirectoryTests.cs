using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using RocketWiki.Core.Entities;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The people-picker directory (<c>userDirectory</c>): readable by any authenticated
/// user, and carrying only the display-safe <see cref="UserRef"/> projection.
///
/// <para>Two different things are under test and they fail in opposite directions. The
/// reachability tests catch the directory being <b>too closed</b> — an admin gate creeping
/// back would break every picker. The exposure test catches it being <b>too open</b> — a
/// field added to <c>UserRef</c> later would silently turn a name-and-avatar list into a
/// census of email addresses or, far worse, of caveats. The second is the one that
/// would not announce itself.</para>
/// </summary>
public sealed class UserDirectoryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string DirectoryQuery =
        "{ userDirectory(first: 50) { totalCount nodes { id displayName hasAvatar } } }";

    private async Task<User> SeedUserAsync(string displayName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var user = new User
        {
            Subject = $"dir-{Guid.NewGuid()}",
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@example.test",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static List<string> NamesIn(JsonDocument result) =>
        result.RootElement.GetProperty("data").GetProperty("userDirectory").GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("displayName").GetString()!)
            .ToList();

    /// <summary>
    /// The point of the feature: a plain authenticated user — no instance-admin role, no
    /// space grants — can read it. An admin gate creeping onto this field would fail here.
    /// </summary>
    [Fact]
    public async Task UserDirectory_IsReadableByAnOrdinaryAuthenticatedUser()
    {
        var seeded = await SeedUserAsync($"Directory Person {Guid.NewGuid():N}"[..24]);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"ordinary-{Guid.NewGuid()}");

        using var result = await client.PostGraphQLAsync(DirectoryQuery);

        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        Assert.Contains(seeded.DisplayName, NamesIn(result));
    }

    [Fact]
    public async Task UserDirectory_FiltersBySearchTerm()
    {
        var token = $"Zzq{Guid.NewGuid():N}"[..12];
        var match = await SeedUserAsync($"Findable {token}");
        var other = await SeedUserAsync($"Unrelated {Guid.NewGuid():N}"[..24]);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"searcher-{Guid.NewGuid()}");

        using var result = await client.PostGraphQLAsync(
            $$"""{ userDirectory(search: "{{token}}", first: 50) { nodes { displayName } } }""");

        var names = NamesIn(result);
        Assert.Contains(match.DisplayName, names);
        Assert.DoesNotContain(other.DisplayName, names);
    }

    [Fact]
    public async Task UserDirectory_IsEmptyForAnonymous_RatherThanErroring()
    {
        await SeedUserAsync($"Hidden From Anonymous {Guid.NewGuid():N}"[..30]);

        var anonymous = factory.CreateClient();
        using var result = await anonymous.PostGraphQLAsync(DirectoryQuery);

        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        Assert.Empty(NamesIn(result));
    }

    /// <summary>
    /// <b>The exposure guard.</b> Pins the directory's row type to exactly the three
    /// display-safe facts, so that adding a field to <see cref="UserRef"/> — which is
    /// shared with comment bylines and would look entirely harmless there — cannot quietly
    /// widen what an ordinary user can enumerate about every colleague.
    ///
    /// <para>Asserted as an exact set rather than a list of forbidden names. A denylist
    /// only catches the leaks somebody thought of; <c>homeSpaceKey</c>, <c>jobTitle</c>,
    /// <c>manager</c> and <c>phone</c> would all sail past one. The rule this encodes is
    /// "this type does not grow without a decision", which is the only version that holds
    /// against fields nobody has invented yet.</para>
    ///
    /// <para>If you are here because this test failed after adding a field: that is the
    /// test working. Decide whether every authenticated user should be able to enumerate
    /// that fact about every colleague — email and last-seen were both deliberately
    /// refused (§15), and any rule-engine attribute is a hard no (§6.2, a who-holds-what
    /// census) — then update this set if the answer is genuinely yes.</para>
    /// </summary>
    [Fact]
    public void UserDirectory_RowType_ExposesOnlyIdDisplayNameAndAvatarFlag()
    {
        var exposed = typeof(UserRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["DisplayName", "HasAvatar", "Id"], exposed);
    }

    /// <summary>
    /// The admin roster stays admin-only. The directory was added <i>beside</i> it rather
    /// than by relaxing it, and this pins that: the same ordinary caller who can read the
    /// directory above must still be refused the rich, email-carrying list.
    /// </summary>
    [Fact]
    public async Task Users_RemainsInstanceAdminOnly_ForTheSameOrdinaryCaller()
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"ordinary-{Guid.NewGuid()}");

        using var directory = await client.PostGraphQLAsync(DirectoryQuery);
        Assert.False(directory.RootElement.TryGetProperty("errors", out _));

        using var roster = await client.PostGraphQLAsync(
            "{ users(first: 5) { nodes { id email } } }");

        Assert.True(
            roster.RootElement.TryGetProperty("errors", out _),
            "the admin roster must still refuse a non-admin who can read the directory");
    }
}
