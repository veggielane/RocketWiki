using System.Text.Json;
using RocketWiki.Core.Access;
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
/// attach/detach, page-property set/remove, attachment add/delete - does.
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
    /// <paramref name="ownershipMismatchEventTypes"/> collects the opposite: every
    /// sync-relevant event NOT journaled because the exported space's origin is another
    /// instance (skip condition 2 below) — commit-gated the same way, since a skip that
    /// rolled back with its mutation never happened.
    /// </summary>
    public static void AppendPendingEvents(
        RocketWikiDbContext db, IReadOnlyList<IDomainEvent> pendingDomainEvents,
        ICollection<SyncEventType> appendedEventTypes, ICollection<SyncEventType> ownershipMismatchEventTypes)
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

            if (!space.IsExported)
            {
                continue;
            }

            // design.md §12: "Exported-ness is a low-side property. Only a native space
            // can be exported; a replica must never emit sync events for content it
            // doesn't own." The writer used to trust Space.IsExported alone; §12's
            // stated follow-up is enforced here: the space must ALSO originate from
            // this instance before anything is journaled. Two failure modes, handled
            // differently on purpose:
            //
            // 1. No local instance id configured at all (UseLocalInstanceId never
            //    called on this context's options): throw. Silently skipping would
            //    silently break sync for content this instance genuinely owns - the
            //    exact "silently incomplete sync stream" the untracked-entity throws
            //    below exist to prevent. Same precedent, same loudness.
            if (db.LocalInstanceId is null)
            {
                throw new InvalidOperationException(
                    $"Domain event '{domainEvent.GetType().Name}' touches exported space '{spaceKey}', but this " +
                    "RocketWikiDbContext has no local instance id (design.md §12). Configure it with " +
                    "UseLocalInstanceId(...) on the context options so the sync outbox writer can verify the " +
                    "space is native before journaling - without it, ownership cannot be checked and nothing " +
                    "is committed.");
            }

            // 2. Configured, but the exported space's origin is another instance: a
            //    replica flagged exported - corrupt state (import writes replicas with
            //    IsExported = false, and no app path flags a replica exported). Skip,
            //    don't throw: the mutation itself can be legitimate (rule management
            //    on a replica is locally scoped and allowed, see design.md §12's
            //    "stays local" table and ReplicaSpace_ProducesNoOutboxEvent... in
            //    SyncOutboxTests), and holding it hostage to a mis-set low-side-only
            //    flag would trade a working wiki for a row that must not exist anyway.
            //    The untracked-entity throws below guard the opposite risk (an
            //    incomplete journal for owned content); here the fail-closed outcome
            //    IS the skip - content this instance doesn't own never enters its
            //    journal, exactly like the !IsExported skip above. But unlike that
            //    skip, this one only ever fires on CORRUPT state, so it must be
            //    operator-visible rather than purely silent: the caller turns the
            //    collected types into rocketwiki.sync.outbox_ownership_mismatches
            //    once the (legitimate, local) mutation actually commits.
            if (!string.Equals(space.OriginInstanceId, db.LocalInstanceId, StringComparison.Ordinal))
            {
                ownershipMismatchEventTypes.Add(eventType.Value);
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

    /// <summary>
    /// Domain events that are deliberately NOT journalled, named as types so the decision
    /// is enforceable rather than only commented.
    ///
    /// <para>This list exists because <see cref="Classify"/>'s default is <c>null</c> —
    /// journal nothing — while its sibling consumer <c>DomainEventAuditMapper.Describe</c>
    /// throws on an unrecognised event. Opposite defaults on the same event stream mean a
    /// new content-bearing event added tomorrow gets audited but <b>silently never
    /// syncs</b>, and silence is the one failure mode §12 cannot tolerate: the high side
    /// has no way to know content is missing. Making the omissions explicit lets
    /// SyncOutboxWriterCoverageTests require every <c>IDomainEvent</c> to be on one list
    /// or the other, so adding an event forces the decision instead of defaulting it.</para>
    ///
    /// <para>Changing the default to "throw" was the alternative and is worse: it would
    /// fail a user's mutation over a sync-classification gap, and §12 is explicit that
    /// sync never blocks authoring on the low side.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, string> DeliberatelyNotJournalled =
        new Dictionary<Type, string>
        {
            [typeof(LabelCreatedEvent)] =
                "Creating a label with nothing attached yet has no content-syncing implication; the import side upserts a Label row by name from an attach event's payload.",
            [typeof(PagePropertyKeyCreatedEvent)] =
                "The property-key registry is instance-local vocabulary with no space to journal against; the import side materializes whatever key a value event names (§20).",
            [typeof(PagePropertyKeyDeletedEvent)] =
                "Same as PagePropertyKeyCreatedEvent: instance-local vocabulary, not content.",

            // Space lifecycle and identity. design.md §12's table is explicit that spaces
            // are not a sync event type at all: import creates the replica row from the
            // space key alone, so renaming, archiving or re-pointing a replica's default
            // page is legitimate local curation on the high side, like grants — not a
            // blocked content write.
            [typeof(SpaceCreatedEvent)] =
                "Spaces are not a sync event type (§12): import materializes the replica space from the space key carried on content events.",
            [typeof(SpaceRenamedEvent)] =
                "Space name and description are local identity, not content (§12); the high side curates its own replica's naming.",
            [typeof(SpaceArchivedEvent)] =
                "Archived state is local curation (§12) — the high side decides what its own replica shows.",
            [typeof(SpaceRestoredEvent)] =
                "Same as SpaceArchivedEvent: local curation of the replica (§12).",
            [typeof(SpaceHomepageSetEvent)] =
                "The default page is a page REFERENCE and each side holds a different subset of pages (§12), so a low-side homepage could name a page the high side has no row for.",
            [typeof(SpaceOwnerChangedEvent)] =
                "The owner is a USER reference, and §12's table keeps users on their own side — each instance has its own Keycloak, so a low-side user id names nobody on high. Accountability for a replica belongs to whoever runs the high side, which is why it is set there rather than inherited.",
            [typeof(SpaceExportChangedEvent)] =
                "Whether a space is exported is the LOW side's own decision about what to send; the high side has no use for it and must never be able to change it.",

            // Per-user state. None of it is content, and all of it is meaningless on the
            // other instance: users are mirrored per instance and authorization always
            // evaluates the token (§6.1), never another instance's copy of a preference.
            [typeof(WatchAddedEvent)] =
                "A watch is one user's subscription on this instance; notifications are instance-local (data-model.md).",
            [typeof(WatchRemovedEvent)] =
                "Same as WatchAddedEvent: per-user subscription state, not content.",
            [typeof(NotificationMarkedReadEvent)] =
                "Notification rows are instance-local and never synced (data-model.md).",
            [typeof(AvatarSetEvent)] =
                "UserAvatar is instance-local (data-model.md) — a profile picture is not content and each instance holds its own.",
            [typeof(AvatarClearedEvent)] =
                "Same as AvatarSetEvent: instance-local profile data.",

            // Credentials must never cross the boundary in either direction. This one is
            // not merely 'no need to sync' — journalling it would put a user's encrypted
            // GitLab token into a bundle that leaves the low side.
            [typeof(GitLabTokenSetEvent)] =
                "GitLabCredential is instance-local and secret (data-model.md); a credential must never enter a bundle that crosses the boundary.",
            [typeof(GitLabTokenClearedEvent)] =
                "Same as GitLabTokenSetEvent: credential state never crosses the boundary.",

            [typeof(CustomEmojiCreatedEvent)] =
                "CustomEmoji is an admin-curated instance-local registry (data-model.md), not page content.",
            [typeof(CustomEmojiDeletedEvent)] =
                "Same as CustomEmojiCreatedEvent: instance-local registry.",

            // The high side's own record of receiving a bundle. Journalling it would
            // re-export the fact of an import back out of the instance that performed it.
            [typeof(SyncImportedEvent)] =
                "This IS the record of an import on the receiving side; journalling it would feed sync back into itself.",
            [typeof(SyncImportRefusedEvent)] =
                "Same as SyncImportedEvent: a receiving-side record, never content to forward.",
        };

    private static SyncEventType? Classify(IDomainEvent domainEvent) => ClassifyCore(domainEvent);


    /// <summary>Internal for the coverage sweep; <see cref="Classify"/> is the call site.</summary>
    internal static SyncEventType? ClassifyCore(IDomainEvent domainEvent) => domainEvent switch
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
        // Page properties travel with content (design.md §20). PagePropertyKeyCreated/
        // Deleted are deliberately absent, for the same reason LabelCreatedEvent is: the
        // registry is instance-local vocabulary with no space to journal against, and the
        // import side materializes whatever key a value event names.
        PagePropertySetEvent => SyncEventType.PageProperties,
        PagePropertyRemovedEvent => SyncEventType.PageProperties,
        // Protective markings travel with content (design.md §21/§12's table). Unlike
        // space grants - which stay local because the high side decides who may read its
        // replica - a marking is a property OF the content: a page that is SECRET on low
        // is SECRET wherever it lands, and letting the high side rediscover that for
        // itself would be exactly the "arrived unmarked" hole this event closes.
        PageMarkingSetEvent => SyncEventType.PageMarking,
        // Entries are a page's structured content, so they cross with it. Their
        // markings cross too, inside the payload, for the same reason a page's does: an
        // entry that is SECRET on low is SECRET wherever it lands.
        PageEntryCreatedEvent => SyncEventType.PageEntry,
        PageEntryUpdatedEvent => SyncEventType.PageEntry,
        PageEntryDeletedEvent => SyncEventType.PageEntry,
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
        PageEntryCreatedEvent e => e.SpaceKey,
        PageEntryUpdatedEvent e => e.SpaceKey,
        PageEntryDeletedEvent e => e.SpaceKey,
        PagePropertySetEvent e => e.SpaceKey,
        PagePropertyRemovedEvent e => e.SpaceKey,
        PageMarkingSetEvent e => e.SpaceKey,
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

        // The key's NAME crosses, never this instance's registry row id: the registry is
        // instance-local (design.md §20), so the receiving instance may have never seen
        // this key and has no row to point at. On import it finds-or-creates the key by
        // normalized name before applying the value - exactly how ApplyLabelAsync matches
        // labels. Sending the id instead would leave the property referencing nothing.
        // pageId stays top-level so CollectAffectedPageIds can reindex/notify the page
        // the same way it does for a label or comment event.
        PagePropertySetEvent e => JsonSerializer.Serialize(
            new { pageId = e.PageId, key = e.Key, value = e.Value, action = "set" }, PayloadOptions),
        PagePropertyRemovedEvent e => JsonSerializer.Serialize(
            new { pageId = e.PageId, key = e.Key, value = (string?)null, action = "remove" }, PayloadOptions),

        // design.md §21. The level crosses as its WIRE NAME (OFFICIAL_SENSITIVE, not the
        // C# spelling and not the tinyint), so a future renumbering of the enum cannot
        // silently re-rank a bundle already sitting on a transfer disk. Only the AFTER
        // state travels: sync replays state, and the receiving instance's own audit log
        // records what it applied - the before/after pair exists for the LOW side's
        // reviewer, in the low side's audit table, which never crosses (§12).
        //
        // The country set crosses verbatim, in canonical order. The receiving instance
        // may well have no matching nationality vocabulary registered, and that is
        // handled the §12 way rather than by dropping the caveat: an unrecognised country
        // matches no principal, so the page arrives MORE restricted, exactly as "a
        // group/attribute unknown on high matches nobody" already works for restrictions.
        // Dropping it would be the one unsafe direction.
        // The national prefix travels too, even though it gates nothing: a replica must
        // render the same marking string as its origin, and "presentational" is exactly
        // why it has to cross - a high-side reader comparing a page against its low-side
        // counterpart would otherwise see two different markings on identical content.
        PageMarkingSetEvent e => JsonSerializer.Serialize(
            new
            {
                pageId = e.PageId,
                level = ProtectiveMarking.LevelWireName(e.After.Level),
                eyesOnly = e.After.EyesOnly,
                prefix = e.After.Prefix,
            },
            PayloadOptions),

        // docs/ENTRIES-AND-FORMS-PLAN.md. The whole entry travels every time, including
        // its marking, and every field is written even when null. That is not verbosity:
        // the import side assigns what it reads, so a payload that OMITTED a field would
        // be read as CLEARING it. That exact bug shipped for page icons - the incremental
        // payload left the field out, and every ordinary edit then stripped an icon the
        // replica had received in its baseline.
        //
        // The level crosses as its WIRE NAME, never the tinyint, so a future renumbering
        // of the enum cannot silently re-rank a bundle already sitting on a transfer
        // disk. The country set crosses verbatim: an unrecognised country on the
        // receiving side matches no principal, so the entry arrives MORE restricted,
        // which is the only safe direction (§12).
        PageEntryCreatedEvent e => SerializePageEntry(RequireTrackedEntry(db, e.EntryId)),
        PageEntryUpdatedEvent e => SerializePageEntry(RequireTrackedEntry(db, e.EntryId)),
        PageEntryDeletedEvent e => SerializePageEntry(RequireTrackedEntry(db, e.EntryId)),

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
            // By NAME, exactly as the baseline exporter writes it, and written even when
            // null. The import side reads an absent key as "no icon", so omitting it here
            // did not merely fail to carry an icon change - it made every incremental
            // page edit strip the icon a replica had received in its baseline.
            icon = page.Icon is null ? null : PageIcons.ToWireName(page.Icon.Value),
            sortOrder = page.SortOrder,
            content = page.CurrentContent, // full Markdown, never a diff (design.md §12)
            revisionNumber = page.CurrentRevisionNumber,
        },
        PayloadOptions);

    private static string SerializePageEntry(PageEntry entry) => JsonSerializer.Serialize(
        new
        {
            entryId = entry.Id,
            pageId = entry.PageId,
            collection = entry.Collection,
            data = entry.Data,
            version = entry.Version,
            level = ProtectiveMarking.LevelWireName(entry.Level),
            eyesOnly = entry.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            prefix = entry.Prefix,
            // Needed on import to apply the tombstone, not inferred from a blank payload -
            // the same reason Comment carries its own IsDeleted.
            isDeleted = entry.IsDeleted,
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

    private static PageEntry RequireTrackedEntry(RocketWikiDbContext db, Guid entryId) =>
        db.ChangeTracker.Entries<PageEntry>().Select(e => e.Entity).FirstOrDefault(e => e.Id == entryId)
        ?? throw new InvalidOperationException(
            $"PageEntry {entryId} is not tracked in this unit of work; the sync outbox writer needs it loaded by the calling service.");

    private static Attachment RequireTrackedAttachment(RocketWikiDbContext db, Guid attachmentId) =>
        db.ChangeTracker.Entries<Attachment>().Select(e => e.Entity).FirstOrDefault(a => a.Id == attachmentId)
        ?? throw new InvalidOperationException(
            $"Attachment {attachmentId} is not tracked in this unit of work; the sync outbox writer needs it loaded by the calling service.");
}
