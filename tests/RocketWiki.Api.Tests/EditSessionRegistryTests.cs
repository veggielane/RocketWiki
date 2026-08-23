using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// Pure-logic tier for the edit-session registry (design.md §8 co-editing, §14's
/// "pure logic in unit tests"): seeder designation, log/cap accounting, reseed
/// pending-ness, contributor sequencing and draining, and empty-session expiry —
/// everything with an invariant, exercised without a hub. The hub-level behaviour
/// (relay, audit rows, canEdit gating) lives in EditSessionHubTests.
/// </summary>
public sealed class EditSessionRegistryTests
{
    private static readonly Principal AnyPrincipal = Principal.Create("sub", []);

    private static EditSessionMember Member(string connectionId, Guid? userId = null) =>
        new(connectionId, userId ?? Guid.NewGuid(), AnyPrincipal, "127.0.0.1");

    private static EditSessionRegistry CreateRegistry(
        FakeTimeProvider? time = null, long logCapBytes = 1024 * 1024, TimeSpan? grace = null)
    {
        var options = new CoEditOptions { LogCapBytes = logCapBytes };
        if (grace is not null)
        {
            options.EmptySessionGrace = grace.Value;
        }

        return new EditSessionRegistry(Options.Create(options), time ?? new FakeTimeProvider());
    }

    [Fact]
    public void FirstJoiner_IsSeeder_AndCreatesSessionAtGivenBaseRevision()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();

        var outcome = registry.Join(pageId, Member("c1"), currentRevisionNumber: 7);

        Assert.True(outcome.IsSeeder);
        Assert.True(outcome.SessionCreated);
        Assert.Equal(7, outcome.BaseRevisionNumber);
        Assert.Empty(registry.GetLogSnapshot(pageId));
    }

    [Fact]
    public void SecondJoiner_IsJoiner_AndSeesTheSessionsBaseRevision_NotItsOwn()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 7);

        // The page hasn't changed, but even if a stale caller passed a different
        // number, the SESSION's base revision (fixed at creation) is what counts.
        var outcome = registry.Join(pageId, Member("c2"), 9);

        Assert.False(outcome.IsSeeder);
        Assert.False(outcome.SessionCreated);
        Assert.Equal(7, outcome.BaseRevisionNumber);
    }

    [Fact]
    public void SeederLeavesBeforeSeeding_NextMemberIsPromoted_WithSeederLostDemand()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 3);
        registry.Join(pageId, Member("c2"), 3);

        var departure = registry.Leave(pageId, "c1");

        Assert.NotNull(departure);
        Assert.NotNull(departure!.Demand);
        Assert.Equal("c2", departure.Demand!.ConnectionId);
        Assert.Equal(ReseedDemand.ReasonSeederLost, departure.Demand.Reason);
        Assert.Equal(3, departure.Demand.BaseRevisionNumber);
    }

    [Fact]
    public void SeederLeavesAfterSeeding_NoDemand_LogIsTheSeed()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 3);
        registry.Join(pageId, Member("c2"), 3);
        registry.AppendUpdate(pageId, "c1", [1, 2, 3]);

        var departure = registry.Leave(pageId, "c1");

        Assert.NotNull(departure);
        Assert.Null(departure!.Demand);
        Assert.Single(registry.GetLogSnapshot(pageId));
    }

    [Fact]
    public void JoinerOfEmptyLogSession_WhoseSeederIsGone_IsPromotedToSeeder()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 3);
        registry.Leave(pageId, "c1"); // session survives in grace, log empty, no seeder

        var outcome = registry.Join(pageId, Member("c2"), 3);

        Assert.True(outcome.IsSeeder);
        Assert.False(outcome.SessionCreated);
    }

    [Fact]
    public void AppendUpdate_FromNonMember_IsRefused()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 1);

        var result = registry.AppendUpdate(pageId, "stranger", [1]);

        Assert.False(result.Accepted);
        Assert.Empty(registry.GetLogSnapshot(pageId)); // nothing was appended
    }

    [Fact]
    public void CrossingTheLogCap_DemandsReseedOnce_PreferringTheSeeder()
    {
        var registry = CreateRegistry(logCapBytes: 10);
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 1);
        registry.Join(pageId, Member("c2"), 1);

        var below = registry.AppendUpdate(pageId, "c2", new byte[8]);
        var crossing = registry.AppendUpdate(pageId, "c2", new byte[8]);
        var after = registry.AppendUpdate(pageId, "c2", new byte[8]);

        Assert.Null(below.Demand);
        Assert.NotNull(crossing.Demand);
        Assert.Equal("c1", crossing.Demand!.ConnectionId); // the seeder, still present
        Assert.Equal(ReseedDemand.ReasonLogCap, crossing.Demand.Reason);
        // Pending: no second demand spam while the first is outstanding - but relay
        // and log continue (refusing appends would fork members' documents).
        Assert.Null(after.Demand);
        Assert.Equal(3, registry.GetLogSnapshot(pageId).Count);
    }

    [Fact]
    public void ApplyReseed_OnlyHonouredForTheDesignatedConnection_ThenReplacesLogAndBaseRevision()
    {
        var registry = CreateRegistry(logCapBytes: 10);
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 1);
        registry.Join(pageId, Member("c2"), 1);
        var crossing = registry.AppendUpdate(pageId, "c2", new byte[16]);
        Assert.Equal("c1", crossing.Demand!.ConnectionId);

        Assert.False(registry.ApplyReseed(pageId, "c2", [9, 9], 2)); // not the designee
        Assert.True(registry.ApplyReseed(pageId, "c1", [9, 9], 2));

        var log = registry.GetLogSnapshot(pageId);
        Assert.Single(log);
        Assert.Equal(new byte[] { 9, 9 }, log[0]);
        // Base revision advanced: the next joiner saves against revision 2.
        Assert.Equal(2, registry.Join(pageId, Member("c3"), 99).BaseRevisionNumber);
        // Pending cleared: a second reseed from the old designee is refused.
        Assert.False(registry.ApplyReseed(pageId, "c1", [1], 3));
    }

    [Fact]
    public void DesignatedReseederLeaving_MovesTheDemandToAnotherMember()
    {
        var registry = CreateRegistry(logCapBytes: 10);
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 1);
        registry.Join(pageId, Member("c2"), 1);
        registry.AppendUpdate(pageId, "c2", new byte[16]); // demand -> c1

        var departure = registry.Leave(pageId, "c1");

        Assert.NotNull(departure!.Demand);
        Assert.Equal("c2", departure.Demand!.ConnectionId);
        Assert.Equal(ReseedDemand.ReasonLogCap, departure.Demand.Reason);
        Assert.True(registry.ApplyReseed(pageId, "c2", [1], 1));
    }

    [Fact]
    public void Contributors_TrackDistinctTypists_AndSaveMembershipIsRequired()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var mallory = Guid.NewGuid();
        registry.Join(pageId, Member("c1", alice), 1);
        registry.Join(pageId, Member("c2", bob), 1);

        registry.AppendUpdate(pageId, "c1", [1]);
        registry.AppendUpdate(pageId, "c2", [2]);
        registry.AppendUpdate(pageId, "c1", [3]);

        // A member who typed nothing can still save and carries both typists.
        var snapshot = registry.SnapshotContributorsForSave(pageId, bob);
        Assert.NotNull(snapshot);
        Assert.Equal(new[] { alice, bob }.OrderBy(g => g), snapshot!.UserIds.OrderBy(g => g));

        // A non-member - even a real user - gets nothing to attach (§7: the session's
        // attribution never decorates a save made outside it).
        Assert.Null(registry.SnapshotContributorsForSave(pageId, mallory));
    }

    [Fact]
    public void OnSaved_DrainsOnlyUpToTheSnapshotSequence()
    {
        var registry = CreateRegistry();
        var pageId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        registry.Join(pageId, Member("c1", alice), 1);
        registry.Join(pageId, Member("c2", bob), 1);

        registry.AppendUpdate(pageId, "c1", [1]);
        var snapshot = registry.SnapshotContributorsForSave(pageId, alice)!;

        // Bob's keystroke lands while Alice's save is in flight.
        registry.AppendUpdate(pageId, "c2", [2]);
        registry.OnSaved(pageId, snapshot.MaxSequence, newRevisionNumber: 2);

        var next = registry.SnapshotContributorsForSave(pageId, alice)!;
        Assert.Equal([bob], next.UserIds); // bob survives the drain; alice is spent
        Assert.Equal(2, registry.Join(pageId, Member("c3"), 99).BaseRevisionNumber);
    }

    [Fact]
    public void EmptySession_SurvivesGrace_ThenIsSwept()
    {
        var time = new FakeTimeProvider();
        var registry = CreateRegistry(time, grace: TimeSpan.FromSeconds(60));
        var pageId = Guid.NewGuid();
        registry.Join(pageId, Member("c1"), 5);
        registry.AppendUpdate(pageId, "c1", [1, 2]);
        registry.Leave(pageId, "c1");

        // Within grace: a re-joiner finds the session (and its log) alive.
        time.Advance(TimeSpan.FromSeconds(30));
        registry.SweepExpired(time.GetUtcNow().UtcDateTime);
        var rejoin = registry.Join(pageId, Member("c2"), 99);
        Assert.False(rejoin.SessionCreated);
        Assert.Single(registry.GetLogSnapshot(pageId));

        // Empty again, past grace: dropped - the next joiner starts fresh.
        registry.Leave(pageId, "c2");
        time.Advance(TimeSpan.FromSeconds(61));
        registry.SweepExpired(time.GetUtcNow().UtcDateTime);
        var fresh = registry.Join(pageId, Member("c3"), 12);
        Assert.True(fresh.SessionCreated);
        Assert.True(fresh.IsSeeder);
        Assert.Equal(12, fresh.BaseRevisionNumber);
        Assert.Empty(registry.GetLogSnapshot(pageId));
    }

    [Fact]
    public void RemoveConnection_DepartsEverySessionTheConnectionWasIn()
    {
        var registry = CreateRegistry();
        var pageA = Guid.NewGuid();
        var pageB = Guid.NewGuid();
        var userId = Guid.NewGuid();
        registry.Join(pageA, Member("c1", userId), 1);
        registry.Join(pageB, Member("c1", userId), 1);
        registry.Join(pageB, Member("c2"), 1);

        var departures = registry.RemoveConnection("c1");

        Assert.Equal(2, departures.Count);
        Assert.Equal(new[] { pageA, pageB }.OrderBy(g => g), departures.Select(d => d.PageId).OrderBy(g => g));
        Assert.All(departures, d => Assert.Equal(userId, d.Member.UserId));
        Assert.Null(registry.GetMember(pageB, "c1"));
        Assert.NotNull(registry.GetMember(pageB, "c2"));
    }
}
