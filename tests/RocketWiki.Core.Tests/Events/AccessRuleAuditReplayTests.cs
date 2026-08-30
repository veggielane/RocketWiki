using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using Xunit;

namespace RocketWiki.Core.Tests.Events;

/// <summary>
/// design.md §7: "AuditEvent is the only record of rule history... it must be complete
/// enough to reconstruct the rule set at any past instant by replay. Enforced by test."
/// This is that test - built through the real pipeline (AccessRuleChangedEvent ->
/// DomainEventAuditMapper -> AuditEvent -> AccessRuleAuditReplay), not by hand-crafting
/// AuditEvent rows, so it also proves the mapper's serialization round-trips correctly.
/// </summary>
public class AccessRuleAuditReplayTests
{
    private static readonly AuditContext Context = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static AuditEvent Change(DateTime timestampUtc, Guid ruleId, AccessRuleSnapshot? before, AccessRuleSnapshot? after)
    {
        var domainEvent = new AccessRuleChangedEvent(ruleId, "ENG", Guid.NewGuid(), before, after);
        return DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, timestampUtc);
    }

    private static AccessRuleSnapshot SpaceGrant(Guid ruleId, Guid spaceId, SpaceRole role, string expressionJson) =>
        new(ruleId, AccessRuleKind.SpaceGrant, spaceId, null, role, null, expressionJson);

    [Fact]
    public void SingleCreate_IsPresentAtOrAfterItsTimestamp()
    {
        var ruleId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var created = SpaceGrant(ruleId, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var events = new[] { Change(t0, ruleId, before: null, after: created) };

        var state = AccessRuleAuditReplay.ReconstructAsOf(events, t0);

        Assert.True(state.ContainsKey(ruleId));
        Assert.Equal(SpaceRole.Viewer, state[ruleId].Role);
    }

    [Fact]
    public void BeforeCreationTimestamp_RuleIsAbsent()
    {
        var ruleId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var created = SpaceGrant(ruleId, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var events = new[] { Change(t0, ruleId, before: null, after: created) };

        var state = AccessRuleAuditReplay.ReconstructAsOf(events, t0.AddSeconds(-1));

        Assert.False(state.ContainsKey(ruleId));
    }

    [Fact]
    public void CreateThenUpdate_AsOfBetweenThem_ReturnsOriginalState_NotFinalState()
    {
        var ruleId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddDays(1);

        var created = SpaceGrant(ruleId, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");
        var updated = SpaceGrant(ruleId, spaceId, SpaceRole.Editor, """{ "group": "engineering" }""");

        var events = new[]
        {
            Change(t0, ruleId, before: null, after: created),
            Change(t1, ruleId, before: created, after: updated),
        };

        var asOfBetween = AccessRuleAuditReplay.ReconstructAsOf(events, t0.AddHours(12));
        var asOfAfterUpdate = AccessRuleAuditReplay.ReconstructAsOf(events, t1);

        Assert.Equal(SpaceRole.Viewer, asOfBetween[ruleId].Role);
        Assert.Equal("""{ "everyone": true }""", asOfBetween[ruleId].ExpressionJson);

        Assert.Equal(SpaceRole.Editor, asOfAfterUpdate[ruleId].Role);
        Assert.Equal("""{ "group": "engineering" }""", asOfAfterUpdate[ruleId].ExpressionJson);
    }

    [Fact]
    public void CreateThenDelete_AsOfBeforeDelete_StillPresent_AsOfAfterDelete_Absent()
    {
        var ruleId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddDays(1);

        var created = SpaceGrant(ruleId, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");

        var events = new[]
        {
            Change(t0, ruleId, before: null, after: created),
            Change(t1, ruleId, before: created, after: null), // deletion
        };

        var beforeDelete = AccessRuleAuditReplay.ReconstructAsOf(events, t1.AddMinutes(-1));
        var afterDelete = AccessRuleAuditReplay.ReconstructAsOf(events, t1);

        Assert.True(beforeDelete.ContainsKey(ruleId));
        Assert.False(afterDelete.ContainsKey(ruleId));
    }

    [Fact]
    public void MultipleRulesInterleaved_EachTrackedIndependently()
    {
        var ruleA = Guid.NewGuid();
        var ruleB = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var aCreated = SpaceGrant(ruleA, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");
        var bCreated = SpaceGrant(ruleB, spaceId, SpaceRole.Editor, """{ "group": "engineering" }""");
        var aDeleted = aCreated;

        var events = new[]
        {
            Change(t0, ruleA, before: null, after: aCreated),
            Change(t0.AddHours(1), ruleB, before: null, after: bCreated),
            Change(t0.AddHours(2), ruleA, before: aDeleted, after: null), // only A is deleted
        };

        var state = AccessRuleAuditReplay.ReconstructAsOf(events, t0.AddHours(3));

        Assert.False(state.ContainsKey(ruleA));
        Assert.True(state.ContainsKey(ruleB));
        Assert.Equal(SpaceRole.Editor, state[ruleB].Role);
    }

    [Fact]
    public void EventsAfterAsOf_AreIgnored()
    {
        var ruleId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var future = t0.AddYears(1);

        var created = SpaceGrant(ruleId, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");
        var deletedLater = SpaceGrant(ruleId, spaceId, SpaceRole.Viewer, """{ "everyone": true }""");

        var events = new[]
        {
            Change(t0, ruleId, before: null, after: created),
            Change(future, ruleId, before: deletedLater, after: null), // a future deletion
        };

        var state = AccessRuleAuditReplay.ReconstructAsOf(events, t0.AddDays(1));

        Assert.True(state.ContainsKey(ruleId)); // the future deletion must not apply yet
    }

    [Fact]
    public void NonPermissionChangeEvents_AreIgnored()
    {
        var pageId = Guid.NewGuid();
        var pageCreated = new PageCreatedEvent(pageId, Guid.NewGuid(), "ENG", Guid.NewGuid(), "Some Page");
        var unrelatedEvent = DomainEventAuditMapper.ToAuditEvent(pageCreated, Context, DateTime.UtcNow);

        var state = AccessRuleAuditReplay.ReconstructAsOf(new[] { unrelatedEvent }, DateTime.UtcNow);

        Assert.Empty(state);
    }

    [Fact]
    public void MalformedDetailsJson_IsSkipped_RatherThanThrowing()
    {
        var malformed = new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Action = "permission.change",
            SubjectType = AuditSubjectType.Rule,
            SubjectId = Guid.NewGuid(),
            Outcome = AuditOutcome.Success,
            Channel = AuditChannel.GraphQl,
            RequestId = "req-1",
            ClientIp = "127.0.0.1",
            DetailsJson = "not valid json {{{",
        };

        var state = AccessRuleAuditReplay.ReconstructAsOf(new[] { malformed }, DateTime.UtcNow);

        // "Skipped" is the actual claim, and only the second assertion makes it. On its
        // own, "did not throw" also passes for a replay that swallowed the parse error
        // and injected a half-built rule into the reconstructed state — which, for a
        // replay used to answer "who could see this page on that date", is worse than
        // the exception it was checking for.
        Assert.Empty(state);
    }
}
