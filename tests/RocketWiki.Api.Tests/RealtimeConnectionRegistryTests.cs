using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// <see cref="RealtimeConnectionRegistry"/>'s first unit tests. It had none: everything
/// that touched it went through <c>NotificationsHubTests</c>, which calls only
/// <c>GetViewers</c> — so the two-tab, reconnect and unresolvable-viewer cases below were
/// unexercised, and both defects they pin were found by reading rather than by failing.
///
/// <para>These are pure in-memory state transitions, so they belong in a unit test rather
/// than behind a hub round trip. What they are really about is that presence being
/// "approximate and self-healing" (the interface's own words) has limits: it may be
/// transiently wrong, it may not be permanently wrong in the direction of leaving someone
/// authorized.</para>
/// </summary>
public sealed class RealtimeConnectionRegistryTests
{
    private static Principal PrincipalFor(string sub) => Principal.Create(sub, []);

    private static PresenceViewer Viewer(string connectionId, Guid userId) =>
        new(connectionId, userId, "Ada", "#abc", HasAvatar: false);

    /// <summary>
    /// The SignalR reconnect race, run for real.
    ///
    /// <para>The old registry kept one principal per USER and maintained it by hand:
    /// unregister scanned <c>_connections</c> for another connection belonging to the same
    /// user and, finding none, deleted the entry. The scan and the delete are not atomic,
    /// so a reconnect publishing its principal between them had that fresh entry deleted
    /// by the OLD connection's teardown. The user stays connected while
    /// <c>GetConnectedPrincipal</c> answers null for the rest of the session — routed down
    /// <c>INotificationDispatcher</c>'s offline branch, no live push, silently, forever.
    ///
    /// <para><b>Stated plainly: this test does not reproduce the race, and nothing here
    /// can.</b> I tried both ways. Ordering the calls does not do it — with both
    /// connections registered before the unregister, the old scan simply finds the
    /// survivor and passes. Driving them concurrently from a barrier does not do it either:
    /// <c>RegisterConnection</c> is two dictionary writes, so it has almost always finished
    /// before the other thread reaches the scan, and the old implementation passes 500
    /// rounds of this. Hitting the window needs a seam inside the class to stall the scan,
    /// which is a worse thing to ship than an untested-by-reproduction fix.</para>
    ///
    /// <para>So what this is: a regression guard for the <b>invariant</b> — a principal
    /// exists exactly while a connection does, under concurrent register/unregister — and
    /// a thread-safety smoke test. The actual defence is structural, in
    /// <c>RealtimeConnectionRegistry</c>: the second map is gone, so there is no
    /// cross-map state left to lose a race over. That is why the fix is a rewrite of the
    /// data structure rather than tighter ordering around it.</para>
    /// </summary>
    [Fact]
    public async Task ConcurrentReconnectAndDisconnect_LeaveThePrincipalConsistentWithTheConnections()
    {
        var userId = Guid.NewGuid();

        for (var round = 0; round < 200; round++)
        {
            var registry = new RealtimeConnectionRegistry();
            var oldConnection = $"old-{round}";
            var newConnection = $"new-{round}";
            registry.RegisterConnection(oldConnection, userId, PrincipalFor("ada"));

            using var bothReady = new Barrier(2);

            var reconnect = Task.Run(() =>
            {
                bothReady.SignalAndWait();
                registry.RegisterConnection(newConnection, userId, PrincipalFor("ada"));
            });

            var disconnect = Task.Run(() =>
            {
                bothReady.SignalAndWait();
                registry.UnregisterConnection(oldConnection);
            });

            await Task.WhenAll(reconnect, disconnect);

            // The reconnected connection is live, so its principal must be resolvable.
            Assert.NotNull(registry.GetConnectedPrincipal(userId));
        }
    }

    /// <summary>The other direction has to keep working: when the LAST connection for a
    /// user goes, the principal must go with it, or the registry would claim someone is
    /// connected forever.</summary>
    [Fact]
    public void UnregisterConnection_OfTheLastConnection_DropsThePrincipal()
    {
        var registry = new RealtimeConnectionRegistry();
        var userId = Guid.NewGuid();

        registry.RegisterConnection("only-conn", userId, PrincipalFor("ada"));
        registry.UnregisterConnection("only-conn");

        Assert.Null(registry.GetConnectedPrincipal(userId));
    }

    /// <summary>Two tabs: closing one must not cut the other's live delivery.</summary>
    [Fact]
    public void UnregisterConnection_WithASecondTabStillOpen_KeepsThePrincipal()
    {
        var registry = new RealtimeConnectionRegistry();
        var userId = Guid.NewGuid();

        registry.RegisterConnection("tab-one", userId, PrincipalFor("ada"));
        registry.RegisterConnection("tab-two", userId, PrincipalFor("ada"));

        registry.UnregisterConnection("tab-one");

        Assert.NotNull(registry.GetConnectedPrincipal(userId));
    }

    /// <summary>
    /// A viewer present on a page with no connection record must appear in the sweep's
    /// working set with a <b>null</b> principal, not be dropped from it.
    ///
    /// <para>This used to inner-join the two maps, so the one viewer whose identity the
    /// registry cannot state was also the one viewer never re-checked and never evicted —
    /// exactly backwards for §6.7. It is reachable: <c>JoinPage</c> registers presence
    /// without repairing a missing connection record, and the hub only calls
    /// <c>RegisterConnection</c> when the local User row already exists.</para>
    /// </summary>
    [Fact]
    public void GetAllPageConnections_IncludesAViewerWithNoConnectionRecord_WithANullPrincipal()
    {
        var registry = new RealtimeConnectionRegistry();
        var pageId = Guid.NewGuid();
        var knownUser = Guid.NewGuid();

        registry.RegisterConnection("known-conn", knownUser, PrincipalFor("ada"));
        registry.JoinPage(pageId, Viewer("known-conn", knownUser));
        // Present, but never registered — the gap the inner join used to hide.
        registry.JoinPage(pageId, Viewer("orphan-conn", Guid.NewGuid()));

        var working = registry.GetAllPageConnections();

        Assert.Equal(2, working.Count);
        Assert.NotNull(Assert.Single(working, c => c.ConnectionId == "known-conn").Principal);
        Assert.Null(Assert.Single(working, c => c.ConnectionId == "orphan-conn").Principal);
    }

    [Fact]
    public void LeavePage_RemovesTheViewer_AndUnregisterReportsEveryPageTheConnectionHeld()
    {
        var registry = new RealtimeConnectionRegistry();
        var firstPage = Guid.NewGuid();
        var secondPage = Guid.NewGuid();
        var userId = Guid.NewGuid();

        registry.RegisterConnection("conn", userId, PrincipalFor("ada"));
        registry.JoinPage(firstPage, Viewer("conn", userId));
        registry.JoinPage(secondPage, Viewer("conn", userId));

        registry.LeavePage(firstPage, "conn");
        Assert.Empty(registry.GetViewers(firstPage));
        Assert.Single(registry.GetViewers(secondPage));

        var affected = registry.UnregisterConnection("conn");
        Assert.Equal([secondPage], affected);
        Assert.Empty(registry.GetViewers(secondPage));
    }
}
