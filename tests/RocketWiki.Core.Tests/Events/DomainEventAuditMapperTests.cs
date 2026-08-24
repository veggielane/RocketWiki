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

    // --- Page properties (design.md §20) ---------------------------------------------
    // All four mappings are covered here deliberately: DomainEventAuditMapper.Describe's
    // fall-through THROWS, so a missing case is a runtime failure on the mutation path
    // rather than a compile error - these are the tests that catch it at build time.

    [Fact]
    public void PagePropertySetEvent_MapsToPagePropertySet_SubjectIsThePage_AndDetailsCarryKeyAndValue()
    {
        var pageId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var domainEvent = new PagePropertySetEvent(pageId, Guid.NewGuid(), "ENG", actorId, keyId, "Owner", "Ada Lovelace");

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("page.property.set", auditEvent.Action);
        // No AuditSubjectType.Property exists (the list is page/space/attachment/
        // comment/rule) - a value change is audited against the page whose metadata
        // changed, the same judgement call the label mappings make.
        Assert.Equal(AuditSubjectType.Page, auditEvent.SubjectType);
        Assert.Equal(pageId, auditEvent.SubjectId);
        Assert.Equal("ENG", auditEvent.SpaceKey);
        Assert.Equal(actorId, auditEvent.UserId);

        // The value belongs in the audit row: §7's record is of what changed, and the
        // audit table is not telemetry (§15's rule is about traces and logs).
        Assert.Contains("\"key\":\"Owner\"", auditEvent.DetailsJson);
        Assert.Contains("\"value\":\"Ada Lovelace\"", auditEvent.DetailsJson);
    }

    [Fact]
    public void PagePropertyRemovedEvent_MapsToPagePropertyRemove_WithKeyOnly()
    {
        var pageId = Guid.NewGuid();
        var domainEvent = new PagePropertyRemovedEvent(pageId, Guid.NewGuid(), "ENG", Guid.NewGuid(), Guid.NewGuid(), "Owner");

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("page.property.remove", auditEvent.Action);
        Assert.Equal(AuditSubjectType.Page, auditEvent.SubjectType);
        Assert.Equal(pageId, auditEvent.SubjectId);
        Assert.Contains("\"key\":\"Owner\"", auditEvent.DetailsJson);
        // No value: the row is gone, and what it used to say is already in the earlier
        // page.property.set row.
        Assert.DoesNotContain("value", auditEvent.DetailsJson);
    }

    [Fact]
    public void PagePropertyKeyCreatedEvent_MapsToPropertyKeyCreate_WithNoSubject_AndTheKeyInDetails()
    {
        var keyId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var domainEvent = new PagePropertyKeyCreatedEvent(keyId, "Review Date", actorId);

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("property_key.create", auditEvent.Action);
        // Registry vocabulary fits no AuditSubjectType - the custom-emoji precedent:
        // subject null, the id in DetailsJson.
        Assert.Null(auditEvent.SubjectType);
        Assert.Null(auditEvent.SubjectId);
        Assert.Null(auditEvent.SpaceKey);
        Assert.Equal(actorId, auditEvent.UserId);
        Assert.Contains("\"key\":\"Review Date\"", auditEvent.DetailsJson);
        Assert.Contains(keyId.ToString(), auditEvent.DetailsJson);
    }

    [Fact]
    public void PagePropertyKeyDeletedEvent_MapsToPropertyKeyDelete_WithNoSubject_AndTheKeyInDetails()
    {
        var keyId = Guid.NewGuid();
        var domainEvent = new PagePropertyKeyDeletedEvent(keyId, "Obsolete", Guid.NewGuid());

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("property_key.delete", auditEvent.Action);
        Assert.Null(auditEvent.SubjectType);
        Assert.Null(auditEvent.SubjectId);
        Assert.Contains("\"key\":\"Obsolete\"", auditEvent.DetailsJson);
        Assert.Contains(keyId.ToString(), auditEvent.DetailsJson);
    }

    [Fact]
    public void SyncImportRefusedEvent_MapsToItsOwnAction_NeverSyncImportPlusDenied()
    {
        // §7: Denied means a principal was refused by a failing restriction; an
        // integrity refusal has neither, so it is a DISTINCT action whose recorded
        // outcome is Success - the refusal itself completed as designed (§12:
        // "a detected error, never a silent absorb").
        var domainEvent = new SyncImportRefusedEvent(
            "low-instance", "bundle-000042.zip", DeclaredBundleNumber: 42,
            Reason: "chain_mismatch", Detail: "manifest hash chain break");

        var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, Context, Timestamp);

        Assert.Equal("sync.import.refused", auditEvent.Action);
        Assert.Equal(AuditOutcome.Success, auditEvent.Outcome);
        Assert.Null(auditEvent.UserId); // system action, like sync.import
        Assert.Null(auditEvent.SubjectType);
        Assert.Contains("\"reason\":\"chain_mismatch\"", auditEvent.DetailsJson);
        Assert.Contains("bundle-000042.zip", auditEvent.DetailsJson);
        Assert.Contains("\"declaredBundleNumber\":42", auditEvent.DetailsJson);
        Assert.Contains("low-instance", auditEvent.DetailsJson);
    }
}
