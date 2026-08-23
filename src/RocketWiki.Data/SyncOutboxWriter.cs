using System.Text.Json;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;

namespace RocketWiki.Data;

/// <summary>
/// design.md §12: the second consumer of the domain-event pipeline - the seam
/// RocketWikiDbContext left for it. Called from the same pre-save hook as the audit
/// writer, over the same pending-events list, so a mutation, its audit row, and its
/// sync outbox row (when one applies) all commit in one transaction.
///
/// Scope of "the journal only" (this task, not milestone 6's bundle export/import):
/// only space grants and space-lifecycle events never produce an entry (§12: grants
/// stay local to each instance, and there's no SyncEventType for space metadata at
/// all). Everything else this instance models a domain event for - page
/// upserts/moves/deletes/restores, comments, page-restriction changes, label
/// attach/detach, attachment add/delete - does.
///
/// Payloads are built from entities already tracked in THIS unit of work, not from the
/// domain event itself - domain events stay lean/audit-focused (see
/// DomainEventAuditMapper), so a page's full current Markdown, for instance, is read
/// straight off the tracked Page entity a mutation service already loaded. A caller
/// that raises a sync-relevant domain event without having loaded the entity it refers
/// to will get a loud InvalidOperationException here, not a silently incomplete sync
/// stream.
///
/// Author identity (design.md §12: "Authors arrive as shadow users") is deliberately
/// NOT resolved to a display name/email here - payloads carry the raw local
/// AuthorUserId/ActorUserId only. Enriching that into shadow-user-creatable identity is
/// the future bundle EXPORT job's responsibility (out of scope here), which can join
/// back to this instance's own Users table at export time; the outbox journal doesn't
/// need to be self-contained for that until bundles are actually built.
/// </summary>
internal static class SyncOutboxWriter
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <paramref name="appendedEventTypes"/> collects the type of every entry actually
    /// appended, for the caller to turn into telemetry once the transaction commits
    /// (design.md §15 — see RocketWikiDbContext.PendingTelemetry for why counting here
    /// would be wrong). Only entries that survive the skip conditions below are recorded,
    /// so the counter reflects the journal, not the events offered to it.
    /// </summary>
    public static void AppendPendingEvents(
        RocketWikiDbContext db, IReadOnlyList<IDomainEvent> pendingDomainEvents, ICollection<SyncEventType> appendedEventTypes)
    {
        foreach (var domainEvent in pendingDomainEvents)
        {
            var eventType = Classify(domainEvent);
            if (eventType is null)
            {
                continue; // not sync-relevant at all (e.g. a SpaceGrant change, a space-lifecycle event)
            }

            var spaceKey = GetSpaceKey(domainEvent);
            if (spaceKey is null)
            {
                continue;
            }

            var space = db.ChangeTracker.Entries<Space>().Select(e => e.Entity).FirstOrDefault(s => s.Key == spaceKey)
                ?? throw new InvalidOperationException(
                    $"Domain event '{domainEvent.GetType().Name}' references space '{spaceKey}', which is not " +
                    "tracked in this unit of work. The sync outbox writer can only see entities the calling " +
                    "service already loaded - load the Space before raising a sync-relevant domain event.");

            // design.md §12: IsExported is documented as a "low side only" flag - a
            // replica should never carry it, so trusting that invariant here (rather
            // than re-deriving instance identity, which would require threading a
            // localInstanceId into RocketWikiDbContext) keeps the context free of that
            // dependency. Only exported, native spaces ever reach this point.
            if (!space.IsExported)
            {
                continue;
            }

            var payloadJson = BuildPayload(db, domainEvent);

            // data-model.md: gap-free per-space sequence, incremented in the same
            // transaction as the event it numbers - never an identity column, which
            // would burn values on a rolled-back transaction and false-alarm the
            // import-side gap detector.
            space.LastOutboxSequence += 1;
            db.SyncOutboxEvents.Add(new SyncOutboxEvent
            {
                SpaceId = space.Id,
                SequenceNumber = space.LastOutboxSequence,
                EventType = eventType.Value,
                PayloadJson = payloadJson,
                CreatedAtUtc = DateTime.UtcNow,
            });
            appendedEventTypes.Add(eventType.Value);
        }
    }

    private static SyncEventType? Classify(IDomainEvent domainEvent) => domainEvent switch
    {
        PageCreatedEvent => SyncEventType.PageUpsert,
        PageContentUpdatedEvent => SyncEventType.PageUpsert,
        PageRevisionRestoredEvent => SyncEventType.PageUpsert,
        PageMovedEvent => SyncEventType.PageMove,
        PageDeletedEvent => SyncEventType.PageDelete,
        PageSubtreeDeletedEvent => SyncEventType.PageDelete,
        PageRestoredEvent => SyncEventType.PageRestore,
        PageSubtreeRestoredEvent => SyncEventType.PageRestore,
        CommentAddedEvent => SyncEventType.Comment,
        CommentEditedEvent => SyncEventType.Comment,
        CommentDeletedEvent => SyncEventType.Comment,
        // Only page restrictions travel with content (design.md §12's table); space
        // grants stay local to each instance, so a SpaceGrant change never syncs.
        AccessRuleChangedEvent e when (e.After ?? e.Before)?.Kind == AccessRuleKind.PageRestriction => SyncEventType.Restrictions,
        // LabelCreatedEvent is deliberately absent: creating a label with nothing
        // attached yet has no content-syncing implication. The import side upserts a
        // Label row by name from an attach event's payload, so nothing is lost.
        LabelAttachedEvent => SyncEventType.Labels,
        LabelDetachedEvent => SyncEventType.Labels,
        AttachmentAddedEvent => SyncEventType.Attachment,
        AttachmentDeletedEvent => SyncEventType.Attachment,
        _ => null,
    };

    private static string? GetSpaceKey(IDomainEvent domainEvent) => domainEvent switch
    {
        PageCreatedEvent e => e.SpaceKey,
        PageContentUpdatedEvent e => e.SpaceKey,
        PageRevisionRestoredEvent e => e.SpaceKey,
        PageMovedEvent e => e.SpaceKey,
        PageDeletedEvent e => e.SpaceKey,
        PageSubtreeDeletedEvent e => e.SpaceKey,
        PageRestoredEvent e => e.SpaceKey,
        PageSubtreeRestoredEvent e => e.SpaceKey,
        CommentAddedEvent e => e.SpaceKey,
        CommentEditedEvent e => e.SpaceKey,
        CommentDeletedEvent e => e.SpaceKey,
        AccessRuleChangedEvent e => e.SpaceKey,
        LabelAttachedEvent e => e.SpaceKey,
        LabelDetachedEvent e => e.SpaceKey,
        AttachmentAddedEvent e => e.SpaceKey,
        AttachmentDeletedEvent e => e.SpaceKey,
        _ => null,
    };

    private static string BuildPayload(RocketWikiDbContext db, IDomainEvent domainEvent) => domainEvent switch
    {
        PageCreatedEvent e => SerializePageUpsert(RequireTrackedPage(db, e.PageId)),
        PageContentUpdatedEvent e => SerializePageUpsert(RequireTrackedPage(db, e.PageId)),
        PageRevisionRestoredEvent e => SerializePageUpsert(RequireTrackedPage(db, e.PageId)),

        PageMovedEvent e => JsonSerializer.Serialize(
            new
            {
                pageId = e.PageId,
                oldParentPageId = e.OldParentPageId,
                newParentPageId = e.NewParentPageId,
                newAncestorPath = RequireTrackedPage(db, e.PageId).AncestorPath,
                newSortOrder = RequireTrackedPage(db, e.PageId).SortOrder,
            },
            PayloadOptions),

        PageDeletedEvent e => JsonSerializer.Serialize(new { rootPageId = e.PageId, pageIds = new[] { e.PageId } }, PayloadOptions),
        PageSubtreeDeletedEvent e => JsonSerializer.Serialize(new { rootPageId = e.RootPageId, pageIds = e.PageIds }, PayloadOptions),
        PageRestoredEvent e => JsonSerializer.Serialize(new { rootPageId = e.PageId, pageIds = new[] { e.PageId } }, PayloadOptions),
        PageSubtreeRestoredEvent e => JsonSerializer.Serialize(new { rootPageId = e.RootPageId, pageIds = e.PageIds }, PayloadOptions),

        CommentAddedEvent e => SerializeComment(RequireTrackedComment(db, e.CommentId)),
        CommentEditedEvent e => SerializeComment(RequireTrackedComment(db, e.CommentId)),
        CommentDeletedEvent e => SerializeComment(RequireTrackedComment(db, e.CommentId)),

        // AccessRuleSnapshot already carries everything (design.md §7); no tracked-entity lookup needed.
        AccessRuleChangedEvent e => JsonSerializer.Serialize(
            new { accessRuleId = e.AccessRuleId, before = e.Before, after = e.After }, PayloadOptions),

        LabelAttachedEvent e => JsonSerializer.Serialize(
            new { pageId = e.PageId, labelName = e.LabelName, action = "attach" }, PayloadOptions),
        LabelDetachedEvent e => JsonSerializer.Serialize(
            new { pageId = e.PageId, labelName = e.LabelName, action = "detach" }, PayloadOptions),

        AttachmentAddedEvent e => SerializeAttachment(RequireTrackedAttachment(db, e.AttachmentId)),
        AttachmentDeletedEvent e => SerializeAttachment(RequireTrackedAttachment(db, e.AttachmentId)),

        _ => throw new NotSupportedException(
            $"No sync payload builder for domain event type '{domainEvent.GetType().Name}' - add one before Classify() routes it here."),
    };

    private static string SerializePageUpsert(Page page) => JsonSerializer.Serialize(
        new
        {
            pageId = page.Id,
            spaceId = page.SpaceId,
            parentPageId = page.ParentPageId,
            ancestorPath = page.AncestorPath,
            slug = page.Slug,
            title = page.Title,
            sortOrder = page.SortOrder,
            content = page.CurrentContent, // full Markdown, never a diff (design.md §12)
            revisionNumber = page.CurrentRevisionNumber,
        },
        PayloadOptions);

    private static string SerializeComment(Comment comment) => JsonSerializer.Serialize(
        new
        {
            commentId = comment.Id,
            pageId = comment.PageId,
            parentCommentId = comment.ParentCommentId,
            body = comment.Body,
            authorUserId = comment.AuthorUserId,
            isDeleted = comment.IsDeleted, // needed on import to apply the tombstone, not inferred from a blank body
        },
        PayloadOptions);

    // Attachment bytes never travel through the outbox journal - only ContentHash
    // does, which the (not-yet-built) bundle export job uses to key the blobs/
    // folder (design.md §12). StorageKey is deliberately excluded: it's an opaque
    // detail of THIS instance's own storage backend and meaningless on the high side.
    private static string SerializeAttachment(Attachment attachment) => JsonSerializer.Serialize(
        new
        {
            attachmentId = attachment.Id,
            pageId = attachment.PageId,
            fileName = attachment.FileName,
            contentType = attachment.ContentType,
            sizeBytes = attachment.SizeBytes,
            contentHash = Convert.ToHexString(attachment.ContentHash),
            isDeleted = attachment.IsDeleted,
            uploadedByUserId = attachment.UploadedByUserId, // arrives as a shadow user on import, same as a comment author
        },
        PayloadOptions);

    private static Page RequireTrackedPage(RocketWikiDbContext db, Guid pageId) =>
        db.ChangeTracker.Entries<Page>().Select(e => e.Entity).FirstOrDefault(p => p.Id == pageId)
        ?? throw new InvalidOperationException(
            $"Page {pageId} is not tracked in this unit of work; the sync outbox writer needs it loaded by the calling service.");

    private static Comment RequireTrackedComment(RocketWikiDbContext db, Guid commentId) =>
        db.ChangeTracker.Entries<Comment>().Select(e => e.Entity).FirstOrDefault(c => c.Id == commentId)
        ?? throw new InvalidOperationException(
            $"Comment {commentId} is not tracked in this unit of work; the sync outbox writer needs it loaded by the calling service.");

    private static Attachment RequireTrackedAttachment(RocketWikiDbContext db, Guid attachmentId) =>
        db.ChangeTracker.Entries<Attachment>().Select(e => e.Entity).FirstOrDefault(a => a.Id == attachmentId)
        ?? throw new InvalidOperationException(
            $"Attachment {attachmentId} is not tracked in this unit of work; the sync outbox writer needs it loaded by the calling service.");
}
