using System.Text.Json;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Events;

/// <summary>
/// Pure translation from a domain event to the AuditEvent row it produces (design.md
/// §7). Outcome is always Success here - denials are audited by the authorization layer
/// at the point access is refused, before a mutation (and therefore a domain event)
/// would ever happen; this pipeline only ever sees completed mutations.
/// </summary>
public static class DomainEventAuditMapper
{
    public static AuditEvent ToAuditEvent(IDomainEvent domainEvent, AuditContext auditContext, DateTime timestampUtc)
    {
        var (action, subjectType, subjectId, spaceKey, detailsJson) = Describe(domainEvent);

        return new AuditEvent
        {
            TimestampUtc = timestampUtc,
            UserId = domainEvent.ActorUserId,
            Action = action,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SpaceKey = spaceKey,
            Outcome = AuditOutcome.Success,
            Channel = auditContext.Channel,
            RequestId = auditContext.RequestId,
            ClientIp = auditContext.ClientIp,
            McpClient = auditContext.McpClient,
            DetailsJson = detailsJson,
        };
    }

    private static (string Action, AuditSubjectType? SubjectType, Guid? SubjectId, string? SpaceKey, string? DetailsJson) Describe(
        IDomainEvent domainEvent) => domainEvent switch
    {
        PageCreatedEvent e => ("page.create", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { title = e.Title })),

        PageContentUpdatedEvent e => ("page.edit", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { revisionNumber = e.RevisionNumber })),

        PageMovedEvent e => ("page.move", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { oldParentPageId = e.OldParentPageId, newParentPageId = e.NewParentPageId })),

        PageDeletedEvent e => ("page.delete", AuditSubjectType.Page, e.PageId, e.SpaceKey, null),

        PageRestoredEvent e => ("page.restore", AuditSubjectType.Page, e.PageId, e.SpaceKey, null),

        // design.md §6.4.1: cascading delete/restore is audited as ONE event covering
        // the whole subtree, not one event per page - only the COUNT is extracted here,
        // deliberately never the page ids themselves (their titles may be restricted).
        // The full PageIds list exists on the event for the sync outbox, not for audit.
        PageSubtreeDeletedEvent e => ("page.delete", AuditSubjectType.Page, e.RootPageId, e.SpaceKey,
            JsonSerializer.Serialize(new { pageCount = e.PageIds.Count })),

        PageSubtreeRestoredEvent e => ("page.restore", AuditSubjectType.Page, e.RootPageId, e.SpaceKey,
            JsonSerializer.Serialize(new { pageCount = e.PageIds.Count })),

        PageRevisionRestoredEvent e => ("page.restoreRevision", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { restoredRevisionNumber = e.RestoredRevisionNumber })),

        CommentAddedEvent e => ("comment.add", AuditSubjectType.Comment, e.CommentId, e.SpaceKey, null),

        CommentEditedEvent e => ("comment.edit", AuditSubjectType.Comment, e.CommentId, e.SpaceKey, null),

        CommentDeletedEvent e => ("comment.delete", AuditSubjectType.Comment, e.CommentId, e.SpaceKey, null),

        // No AuditSubjectType.Label exists in the schema (data-model.md lists page /
        // space / attachment / comment / rule only) - judgement call: label creation is
        // audited against the Space it belongs to, attach/detach against the Page whose
        // label set changed, since that's the more meaningful "what changed" either way.
        LabelCreatedEvent e => ("label.create", AuditSubjectType.Space, e.SpaceId, e.SpaceKey,
            JsonSerializer.Serialize(new { name = e.Name })),

        LabelAttachedEvent e => ("label.attach", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { labelName = e.LabelName })),

        LabelDetachedEvent e => ("label.detach", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { labelName = e.LabelName })),

        AttachmentAddedEvent e => ("attachment.upload", AuditSubjectType.Attachment, e.AttachmentId, e.SpaceKey,
            JsonSerializer.Serialize(new { fileName = e.FileName })),

        AttachmentDeletedEvent e => ("attachment.delete", AuditSubjectType.Attachment, e.AttachmentId, e.SpaceKey, null),

        // design.md §7: full before/after rule state, never a diff - AuditEvent is the
        // only record of rule history (temporal tables were rejected; data-model.md),
        // so this must be replayable. See AccessRuleAuditReplay for the reader side and
        // AccessRuleAuditJson for why the same JsonSerializerOptions instance is reused here.
        AccessRuleChangedEvent e => ("permission.change", AuditSubjectType.Rule, e.AccessRuleId, e.SpaceKey,
            JsonSerializer.Serialize(new AccessRuleChangeDetails(e.AccessRuleId, e.Before, e.After), AccessRuleAuditJson.Options)),

        SpaceCreatedEvent e => ("space.create", AuditSubjectType.Space, e.SpaceId, e.Key, null),

        SpaceRenamedEvent e => ("space.rename", AuditSubjectType.Space, e.SpaceId, e.Key,
            JsonSerializer.Serialize(new { oldName = e.OldName, newName = e.NewName })),

        SpaceArchivedEvent e => ("space.archive", AuditSubjectType.Space, e.SpaceId, e.Key, null),

        SpaceRestoredEvent e => ("space.restore", AuditSubjectType.Space, e.SpaceId, e.Key, null),

        // design.md §12: "every import is audited (sync.import with bundle id and
        // event range)". No single SubjectType/SubjectId fits - a bundle can span
        // multiple spaces - so both are null here and the full detail lives in
        // DetailsJson, which the audit log viewer can still filter/search.
        SyncImportedEvent e => ("sync.import", null, null, null, JsonSerializer.Serialize(new
        {
            originInstanceId = e.OriginInstanceId,
            bundleNumber = e.BundleNumber,
            wasDuplicate = e.WasDuplicate,
            spaceRanges = e.SpaceRanges,
        })),

        _ => throw new NotSupportedException(
            $"No audit mapping registered for domain event type '{domainEvent.GetType().Name}'. " +
            "Every mutation must be auditable (design.md §7) - add a case here before raising a new event type."),
    };
}
