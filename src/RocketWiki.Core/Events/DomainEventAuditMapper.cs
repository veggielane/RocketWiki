using System.Text.Json;
using RocketWiki.Core.Access;
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

        // Session saves (design.md §8 co-editing) add the contributor list to the
        // details - the audit row is the regulated record, and "whose keystrokes are
        // in this revision" belongs exactly here (§7). Solo saves keep the original
        // shape: an absent key means "no session", not "empty session".
        PageContentUpdatedEvent e => ("page.edit", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            e.ContributorUserIds is { Count: > 0 }
                ? JsonSerializer.Serialize(new { revisionNumber = e.RevisionNumber, contributors = e.ContributorUserIds })
                : JsonSerializer.Serialize(new { revisionNumber = e.RevisionNumber })),

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

        // Page properties (design.md §20). Same judgement call as the label mappings
        // above: AuditSubjectType is a closed list (page / space / attachment / comment
        // / rule) with no Property member, so a value change is audited against the PAGE
        // whose metadata changed - the more meaningful "what changed" either way - and
        // the action name carries the distinction.
        //
        // The VALUE is in DetailsJson deliberately, and it is worth being explicit about
        // why that is not the §15 telemetry rule being bent: §15 forbids page content and
        // attribute values in traces and logs because those are an operational side
        // channel with their own retention and their own (wider) audience. The audit
        // table is the opposite - it is the regulated record of who did what (§7), it is
        // access-controlled like the content it describes, and "what did this property
        // become" is precisely the change being recorded. A property.set row that did not
        // say what was set would be a log line, not an audit record. Remove carries no
        // value: the row is gone, and the previous value is already in the earlier set row.
        PagePropertySetEvent e => ("page.property.set", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { key = e.Key, value = e.Value })),

        PagePropertyRemovedEvent e => ("page.property.remove", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { key = e.Key })),

        // Page entries (docs/ENTRIES-AND-FORMS-PLAN.md). Subject is the Page, like every
        // other per-page metadata change - AuditSubjectType is a closed list with no
        // member for an entry, and the page is the meaningful "what was touched" anyway.
        // Details carry the entry id and collection but NOT the data or the marking
        // level: see the events' own doc for why each is left out.
        PageEntryCreatedEvent e => ("page.entry.create", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { entryId = e.EntryId, collection = e.Collection })),

        PageEntryUpdatedEvent e => ("page.entry.update", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { entryId = e.EntryId, collection = e.Collection })),

        PageEntryDeletedEvent e => ("page.entry.delete", AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new { entryId = e.EntryId, collection = e.Collection })),

        // Protective markings (design.md §21). Subject is the Page, like every other
        // per-page metadata change - AuditSubjectType is a closed list with no member for
        // a marking, and the page whose classification changed is the meaningful "what
        // changed" anyway.
        //
        // TWO ACTIONS from one event, on purpose. Downgrading - lowering the level, or
        // relaxing the eyes-only caveat so somebody who could not read the page now can -
        // is permitted but is the operationally risky direction, so it gets its own action
        // name and a reviewer can find every widening in the estate with one query rather
        // than by diffing before/after on every marking row that ever changed. The
        // predicate itself lives on ProtectiveMarking so the mapper, the tests, and any
        // future reviewer tooling all agree on what "downgrade" means.
        //
        // The from->to marking is in DetailsJson for exactly the reason the property
        // value is (see above): the audit table is the regulated record of who did what,
        // and a marking-change row that did not say what the marking became would be a
        // log line, not an audit record. Note this is also the ONLY place the eyes-only
        // country set is written out in full - §15 keeps it out of every telemetry tag,
        // and the denial reason (caveat:eyes_only) deliberately names no country.
        PageMarkingSetEvent e => (
            e.IsDowngrade ? "page.marking.downgrade" : "page.marking.set",
            AuditSubjectType.Page, e.PageId, e.SpaceKey,
            JsonSerializer.Serialize(new
            {
                level = ProtectiveMarking.LevelWireName(e.After.Level),
                eyesOnly = e.After.EyesOnly,
                // The national prefix is recorded even though it gates nothing
                // (design.md §21.12): a prefix change IS a change to the marking, and an
                // audit row that omitted it would leave a reviewer unable to explain why
                // a page's rendered marking changed. Note this is the ONLY consumer of
                // the prefix outside the display string - it never reaches a denial
                // reason or a telemetry tag.
                prefix = e.After.Prefix,
                previousLevel = ProtectiveMarking.LevelWireName(e.Before.Level),
                previousEyesOnly = e.Before.EyesOnly,
                previousPrefix = e.Before.Prefix,
            })),

        // Registry-level actions follow the custom-emoji precedent exactly: no
        // AuditSubjectType fits instance-local vocabulary (the subject list is wiki
        // content shapes), so subject stays null and the key is named in DetailsJson.
        // The name is the id an auditor actually recognizes; the row id rides along for
        // joinability while the row exists.
        PagePropertyKeyCreatedEvent e => ("property_key.create", null, null, null,
            JsonSerializer.Serialize(new { key = e.Key, propertyKeyId = e.PropertyKeyId })),

        PagePropertyKeyDeletedEvent e => ("property_key.delete", null, null, null,
            JsonSerializer.Serialize(new { key = e.Key, propertyKeyId = e.PropertyKeyId })),

        AttachmentAddedEvent e => ("attachment.upload", AuditSubjectType.Attachment, e.AttachmentId, e.SpaceKey,
            JsonSerializer.Serialize(new { fileName = e.FileName })),

        AttachmentDeletedEvent e => ("attachment.delete", AuditSubjectType.Attachment, e.AttachmentId, e.SpaceKey, null),

        // design.md §7: full before/after rule state, never a diff - AuditEvent is the
        // only record of rule history (temporal tables were rejected; data-model.md),
        // so this must be replayable. See AccessRuleAuditReplay for the reader side and
        // AccessRuleAuditJson for why the same JsonSerializerOptions instance is reused here.
        AccessRuleChangedEvent e => ("permission.change", AuditSubjectType.Rule, e.AccessRuleId, e.SpaceKey,
            JsonSerializer.Serialize(new AccessRuleChangeDetails(e.AccessRuleId, e.Before, e.After), AccessRuleAuditJson.Options)),

        // design.md §8: "watch changes are audited as user actions". Subject is the
        // watched thing itself (page or space — the Watch row's xor guarantees exactly
        // one), not the Watch row id, which is meaningless to an auditor.
        WatchAddedEvent e => ("watch.add", e.PageId is not null ? AuditSubjectType.Page : AuditSubjectType.Space,
            e.PageId ?? e.SpaceId, e.SpaceKey, null),

        WatchRemovedEvent e => ("watch.remove", e.PageId is not null ? AuditSubjectType.Page : AuditSubjectType.Space,
            e.PageId ?? e.SpaceId, e.SpaceKey, null),

        // No AuditSubjectType fits a Notification (data-model.md's subject list is
        // page/space/attachment/comment/rule, and the bigint row id isn't a Guid
        // anyway) - the id goes in DetailsJson instead, same pattern as sync.import.
        NotificationMarkedReadEvent e => ("notification.markRead", null, null, null,
            JsonSerializer.Serialize(new { notificationId = e.NotificationId })),

        // GitLab integration: the credential row is the subject conceptually, but no
        // AuditSubjectType fits a per-user setting (the subject list is wiki content
        // shapes) and the row PK is the acting UserId the event already carries in its
        // UserId column — so subject stays null, same pattern as notification.markRead.
        // Deliberately no DetailsJson: there is nothing to say about a token that
        // wouldn't risk saying too much, and the event type carries no material anyway.
        GitLabTokenSetEvent => ("settings.gitlab_token.set", null, null, null, null),

        GitLabTokenClearedEvent => ("settings.gitlab_token.cleared", null, null, null, null),

        // Profile pictures: same shape as the GitLab settings events — subject null
        // (a per-user setting fits no wiki-content SubjectType; the UserId column
        // already names whose avatar), and deliberately no DetailsJson: nothing about
        // the image belongs in a row (design.md §7 records that it changed, not what
        // it looks like), and the events carry nothing but the actor anyway.
        AvatarSetEvent => ("settings.avatar.set", null, null, null, null),

        AvatarClearedEvent => ("settings.avatar.cleared", null, null, null, null),
        // Custom emojis: no AuditSubjectType fits (the subject list is wiki content
        // shapes and the registry is instance-local vocabulary), so subject stays null
        // and the emoji is named in DetailsJson - the same pattern as
        // notification.markRead. The name is the id an auditor actually recognizes;
        // the row id rides along for joinability while the row exists.
        CustomEmojiCreatedEvent e => ("emoji.created", null, null, null,
            JsonSerializer.Serialize(new { name = e.Name, emojiId = e.EmojiId })),

        CustomEmojiDeletedEvent e => ("emoji.deleted", null, null, null,
            JsonSerializer.Serialize(new { name = e.Name, emojiId = e.EmojiId })),

        SpaceCreatedEvent e => ("space.create", AuditSubjectType.Space, e.SpaceId, e.Key, null),

        SpaceRenamedEvent e => ("space.rename", AuditSubjectType.Space, e.SpaceId, e.Key,
            JsonSerializer.Serialize(new { oldName = e.OldName, newName = e.NewName })),

        // Both ids, not just the new one: the homepage is a single mutable column, so
        // re-deriving what it used to be after the fact is impossible - the audit row is
        // the only history there is (the same reason §21.7 records a marking's
        // before-state). Page ids only, never titles: a title is content.
        SpaceHomepageSetEvent e => ("space.homepage.set", AuditSubjectType.Space, e.SpaceId, e.Key,
            JsonSerializer.Serialize(new { oldPageId = e.OldPageId, newPageId = e.NewPageId })),

        // Both owners, for the same reason the homepage carries both: a single mutable
        // column has no history but this row. Ids only — a display name is a mirrored,
        // mutable label, and §7 should pin who was accountable, not what they were called.
        SpaceOwnerChangedEvent e => ("space.owner.set", AuditSubjectType.Space, e.SpaceId, e.Key,
            JsonSerializer.Serialize(new { oldOwnerUserId = e.OldOwnerUserId, newOwnerUserId = e.NewOwnerUserId })),

        // design.md §12: two action names for one event, deriving which from the event
        // rather than minting two event types with identical payloads — exactly how
        // PageMarkingSetEvent splits set from downgrade. Enabling export is the direction
        // that starts content crossing a security boundary, so it gets its own action
        // name and a reviewer can find every one of them with a single query.
        SpaceExportChangedEvent e => (
            e.Exported ? "space.export.enabled" : "space.export.disabled",
            AuditSubjectType.Space, e.SpaceId, e.Key,
            JsonSerializer.Serialize(new { exported = e.Exported })),

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

        // Deliberately NOT `sync.import` + Denied. §7's outcome vocabulary is
        // success/denied, and Denied specifically means an access decision refused a
        // principal - it is "recorded along with which restriction failed" and feeds
        // the permission inspector. An integrity refusal has no principal and no
        // failing restriction; labeling it Denied would file bundle tampering among
        // access denials and make the permission inspector lie. The action being
        // audited here is the refusal itself - §12's "detected error, never a silent
        // absorb" - and that action completed exactly as designed, so it gets its own
        // action name with Outcome.Success (this pipeline's invariant anyway), keeping
        // "sync.import + success" unambiguously meaning content landed.
        SyncImportRefusedEvent e => ("sync.import.refused", null, null, null, JsonSerializer.Serialize(new
        {
            originInstanceId = e.OriginInstanceId,
            bundleFileName = e.BundleFileName,
            declaredBundleNumber = e.DeclaredBundleNumber,
            reason = e.Reason,
            detail = e.Detail,
        })),

        _ => throw new NotSupportedException(
            $"No audit mapping registered for domain event type '{domainEvent.GetType().Name}'. " +
            "Every mutation must be auditable (design.md §7) - add a case here before raising a new event type."),
    };
}
