using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using Xunit;

namespace RocketWiki.Core.Tests.Events;

public class DomainEventAuditMapperTests
{
    private static readonly AuditContext Context = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");
    private static readonly DateTime Timestamp = new(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PageCreatedEvent_MapsToPageCreateAction()
    {
        var actorId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var domainEvent = new PageCreatedEvent(pageId, Guid.NewGuid(), "ENG", actorId, "Getting Started");

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("page.create", auditEvent.Action);
        Assert.Equal(AuditSubjectType.Page, auditEvent.SubjectType);
        Assert.Equal(pageId, auditEvent.SubjectId);
        Assert.Equal("ENG", auditEvent.SpaceKey);
        Assert.Equal(actorId, auditEvent.UserId);
        Assert.Equal(AuditOutcome.Success, auditEvent.Outcome);
        Assert.Contains("Getting Started", auditEvent.DetailsJson);
    }

    [Fact]
    public void PageMovedEvent_MapsToPageMoveAction_WithOldAndNewParentInDetails()
    {
        var oldParent = Guid.NewGuid();
        var newParent = Guid.NewGuid();
        var domainEvent = new PageMovedEvent(Guid.NewGuid(), Guid.NewGuid(), "ENG", Guid.NewGuid(), oldParent, newParent);

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("page.move", auditEvent.Action);
        Assert.Contains(oldParent.ToString(), auditEvent.DetailsJson);
        Assert.Contains(newParent.ToString(), auditEvent.DetailsJson);
    }

    [Fact]
    public void AccessRuleChangedEvent_MapsToPermissionChangeAction_SubjectTypeRule()
    {
        var ruleId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var after = new AccessRuleSnapshot(ruleId, AccessRuleKind.PageRestriction, null, pageId, null, PageAction.View, """{"group":"engineering"}""");
        var domainEvent = new AccessRuleChangedEvent(ruleId, "ENG", Guid.NewGuid(), Before: null, After: after);

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("permission.change", auditEvent.Action);
        Assert.Equal(AuditSubjectType.Rule, auditEvent.SubjectType);
        Assert.Equal(ruleId, auditEvent.SubjectId);
        Assert.Contains("engineering", auditEvent.DetailsJson);
    }

    [Fact]
    public void PageSubtreeDeletedEvent_MapsToPageDeleteAction_WithPageCountInDetails()
    {
        var rootId = Guid.NewGuid();
        var pageIds = new[] { rootId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var domainEvent = new PageSubtreeDeletedEvent(rootId, Guid.NewGuid(), "ENG", Guid.NewGuid(), pageIds);

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("page.delete", auditEvent.Action);
        Assert.Equal(rootId, auditEvent.SubjectId);
        Assert.Contains("\"pageCount\":4", auditEvent.DetailsJson);

        // design.md §6.4.1: reports how many, never which - the non-root descendant
        // ids must not leak into the audit row even though the event itself carries them.
        foreach (var descendantId in pageIds.Skip(1))
        {
            Assert.DoesNotContain(descendantId.ToString(), auditEvent.DetailsJson);
        }
    }

    [Fact]
    public void PageSubtreeRestoredEvent_MapsToPageRestoreAction_WithPageCountInDetails()
    {
        var rootId = Guid.NewGuid();
        var pageIds = new[] { rootId, Guid.NewGuid() };
        var domainEvent = new PageSubtreeRestoredEvent(rootId, Guid.NewGuid(), "ENG", Guid.NewGuid(), pageIds);

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("page.restore", auditEvent.Action);
        Assert.Equal(rootId, auditEvent.SubjectId);
        Assert.Contains("\"pageCount\":2", auditEvent.DetailsJson);
    }

    [Fact]
    public void SystemInitiatedEvent_NullActorUserId_MapsToNullUserId()
    {
        // design.md §7: UserId is null for system actors (e.g. sync CLI).
        var domainEvent = new PageDeletedEvent(Guid.NewGuid(), Guid.NewGuid(), "ENG", ActorUserId: null);

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Null(auditEvent.UserId);
    }

    [Fact]
    public void AuditContext_PropagatesChannelRequestIdAndClientIp()
    {
        var context = new AuditContext(AuditChannel.Mcp, "req-42", "10.0.0.5", McpClient: "claude-desktop");
        var domainEvent = new CommentAddedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ENG", Guid.NewGuid());

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, context, Timestamp);

        Assert.Equal(AuditChannel.Mcp, auditEvent.Channel);
        Assert.Equal("req-42", auditEvent.RequestId);
        Assert.Equal("10.0.0.5", auditEvent.ClientIp);
        Assert.Equal("claude-desktop", auditEvent.McpClient);
    }

    [Fact]
    public void TimestampUtc_IsTheProvidedTimestamp_NotWallClockTimeOfTheCall()
    {
        var domainEvent = new PageRestoredEvent(Guid.NewGuid(), Guid.NewGuid(), "ENG", Guid.NewGuid());

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal(Timestamp, auditEvent.TimestampUtc);
    }
}
