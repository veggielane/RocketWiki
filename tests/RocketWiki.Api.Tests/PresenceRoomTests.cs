using RocketWiki.Api.RealTime;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The room key is fully client-controlled, so every branch of the parser is reachable by
/// an attacker. Two properties matter more than the parsing itself:
///
/// <list type="number">
/// <item>An unrecognised key must FAIL, not fall through to a permissive default —
/// because the prefix is what selects the authorization, a key that parses to nothing
/// would otherwise be a room with no gate.</item>
/// <item>The <c>site:</c> allowlist bounds the key space. Without it any signed-in caller
/// mints unlimited distinct rooms in an in-memory dictionary just by varying a
/// string.</item>
/// </list>
/// </summary>
public class PresenceRoomTests
{
    [Fact]
    public void APageKeyParsesToThatPage()
    {
        var pageId = Guid.NewGuid();

        Assert.True(PresenceRoom.TryParse($"page:{pageId}", out var room));
        Assert.Equal(pageId, Assert.IsType<PresenceRoom.Page>(room).PageId);
        Assert.Equal($"page:{pageId}", room.Key);
    }

    [Fact]
    public void ASpaceKeyIsCanonicalisedSoOneSpaceIsOneRoom()
    {
        // Space keys are case-insensitive in URLs (§17). Two casings must not become two
        // half-populated rooms in which neither group sees the other.
        Assert.True(PresenceRoom.TryParse("space:eng:browse", out var lower));
        Assert.True(PresenceRoom.TryParse("space:ENG:browse", out var upper));

        Assert.Equal(upper.Key, lower.Key);
        Assert.Equal("ENG", Assert.IsType<PresenceRoom.Space>(lower).SpaceKey);
    }

    [Fact]
    public void SpaceScreensShareAuthorizationButAreDifferentRooms()
    {
        // The contract the SPA actually sends. Both screens of one space need the SAME
        // permission — so the gate reads SpaceKey alone — but a cursor at (0.5, 0.5) on
        // the browser means nothing on the trash screen, so they must be separate
        // SignalR groups.
        Assert.True(PresenceRoom.TryParse("space:ENG:browse", out var browse));
        Assert.True(PresenceRoom.TryParse("space:ENG:trash", out var trash));

        var browseRoom = Assert.IsType<PresenceRoom.Space>(browse);
        var trashRoom = Assert.IsType<PresenceRoom.Space>(trash);

        // Same authorization subject...
        Assert.Equal("ENG", browseRoom.SpaceKey);
        Assert.Equal("ENG", trashRoom.SpaceKey);

        // ...different rooms.
        Assert.NotEqual(browseRoom.Key, trashRoom.Key);
        Assert.Equal("space:ENG:browse", browseRoom.Key);
        Assert.Equal("space:ENG:trash", trashRoom.Key);
    }

    [Fact]
    public void TheScreenSegmentIsNotCanonicalised()
    {
        // It is the client's own layout label, not a resource. Upper-casing it would
        // silently merge screens the SPA considers distinct.
        Assert.True(PresenceRoom.TryParse("space:eng:Import-Report", out var room));
        Assert.Equal("Import-Report", Assert.IsType<PresenceRoom.Space>(room).Screen);
        Assert.Equal("space:ENG:Import-Report", room.Key);
    }

    [Fact]
    public void ABareSpaceKeyIsItsOwnRoom()
    {
        Assert.True(PresenceRoom.TryParse("space:ENG", out var room));
        var space = Assert.IsType<PresenceRoom.Space>(room);
        Assert.Equal("ENG", space.SpaceKey);
        Assert.Null(space.Screen);
        Assert.Equal("space:ENG", space.Key);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/search")]
    [InlineData("/admin/users")]
    [InlineData("/-/docs/classification")]
    [InlineData("/spaces/archived")]
    public void AnySaneSitePathParses(string route)
    {
        // No allowlist, deliberately: a site path names a client SCREEN, not a resource.
        // It carries no page or space id, so there is nothing to authorize against and
        // nothing to leak — a forged path just makes an isolated room its author is alone
        // in. Validating against the real route table would be brittle for open-ended
        // routes (docs topics) and buy no security; the §6.7 gating lives on page: and
        // space:, where the resource actually is.
        Assert.True(PresenceRoom.TryParse($"site:{route}", out var room));
        Assert.Equal(route, Assert.IsType<PresenceRoom.Site>(room).Route);
    }

    [Theory]
    // An empty site path is still refused: the key must name something.
    [InlineData("site:")]
    // Unknown prefixes must not fall through to "authenticated is enough".
    [InlineData("everyone:all")]
    [InlineData("user:00000000-0000-0000-0000-000000000000")]
    [InlineData("edit:00000000-0000-0000-0000-000000000000")]
    // Malformed.
    [InlineData("page:not-a-guid")]
    [InlineData("space:")]
    [InlineData(":home")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? raw)
    {
        Assert.False(PresenceRoom.TryParse(raw, out var room));
        Assert.Null(room);
    }

    [Fact]
    public void AnAbsurdlyLongKeyIsRefusedBeforeAnythingElse()
    {
        // Parsing happens before authorization, so an unbounded key would be a
        // memory-growth vector even for a room that is ultimately refused.
        Assert.False(PresenceRoom.TryParse("site:" + new string('a', 5000), out _));
    }

    [Fact]
    public void ABareGuidIsTheTransitionalPageRoom()
    {
        // SignalR forbids method overloading, so the hub cannot keep both
        // PointerMove(Guid,…) and PointerMove(string,…). The old client call arrives as a
        // GUID-shaped JSON string, and this branch is what keeps it landing on the right
        // room until the SPA sends room keys. Delete it with the adapters.
        var pageId = Guid.NewGuid();

        Assert.True(PresenceRoom.TryParse(pageId.ToString(), out var room));
        Assert.Equal(pageId, Assert.IsType<PresenceRoom.Page>(room).PageId);
        Assert.Equal($"page:{pageId}", room.Key);
    }

    /// <summary>
    /// The gate in <c>NotificationsHub.IsRoomJoinableAsync</c> switches over the three
    /// room types and falls through to <c>false</c>. That default is the right shape but
    /// it is <b>unreachable</b> — the hierarchy is closed (private constructor, sealed
    /// subtypes), so no test can reach it, and mutating it to <c>true</c> passes every
    /// other test in this suite. I checked.
    ///
    /// <para>This is what CAN be enforced instead: adding a fourth room type breaks
    /// here, and the failure message says where the gate lives. Without it, a new type
    /// would silently take the default arm — which fails closed, so presence would just
    /// never work for it, quietly.</para>
    /// </summary>
    [Fact]
    public void EveryRoomTypeIsOneTheGateKnowsAbout()
    {
        var roomTypes = typeof(PresenceRoom).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(PresenceRoom)))
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Page", "Site", "Space"], roomTypes);
    }
}
