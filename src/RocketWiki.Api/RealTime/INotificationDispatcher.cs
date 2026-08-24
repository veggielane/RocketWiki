using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Access;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using RocketWiki.Data.Telemetry;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// design.md §8's per-user fan-out for durable notifications: compute candidate
/// recipients (watchers of the page or its space, plus users newly mentioned in the
/// saved Markdown, minus the actor), then split by connectivity. Mentions are
/// parsed from the <c>@[display](user://{id})</c> Markdown form (§4, MentionParser);
/// on a page edit only mentions ABSENT from the previous revision notify, so re-saving
/// a page doesn't re-ping everyone already mentioned in it. A user who is both a
/// watcher and newly mentioned gets one notification, the more specific Mention — and
/// that precedence is resolved over the WHOLE candidate set before connectivity is
/// consulted, so it holds across the connected/offline split, not per-half.
///
/// **Connected recipients** (a live registered Principal via
/// <see cref="IRealtimeConnectionRegistry"/>): canView is evaluated AT SEND TIME and
/// only those who pass get a persisted row (with the title snapshot) plus a live push.
///
/// **Offline recipients**: canView requires a live ABAC <see cref="Principal"/> built
/// from a validated token (design.md §6.1), and there is no substitute for an offline
/// recipient's token — the local <c>User</c> row is explicitly not a valid stand-in
/// for an authorization decision anywhere else in this system (§6.1's whole point),
/// and manufacturing one here would be the same anti-pattern with extra steps. So no
/// authorization decision is made and NOTHING is disclosed: a deferred row is
/// persisted with <c>TitleSnapshot</c> null, and both the row's EXISTENCE and its
/// title are resolved at the recipient's next notifications fetch against their live
/// token-built Principal (NotificationReadModelService.SurvivesReadTimeCheck) — the
/// same deferred pattern design.md §8 established for sync-imported rows. This is how
/// "notifications are also persisted so users who were offline catch up" is realized
/// without ever trading away fail-closed authorization.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>Page saved (create, edit, revision restore): notifies watchers with <paramref name="type"/> and newly-mentioned users with <see cref="NotificationType.Mention"/>.</summary>
    Task NotifyPageChangedAsync(Guid pageId, Guid actorUserId, NotificationType type, CancellationToken cancellationToken);

    /// <summary>
    /// Comment added: notifies users mentioned in the comment body with
    /// <see cref="NotificationType.Mention"/> and — when the comment is a reply — the
    /// parent comment's author with <see cref="NotificationType.CommentReply"/>
    /// (design.md §8: "someone replied to your comment"). Never the actor themselves,
    /// and a parent author who is also newly mentioned gets ONE notification, the more
    /// specific Mention — the same most-specific-type-wins rule as mention-beats-watch
    /// on page saves. Page/space watchers are deliberately NOT notified here:
    /// page_watched_changed means the page's content changed (§8's "a page you watch
    /// changed"), and comment activity has never fanned out to watchers on this
    /// dispatcher — preserved, not an oversight.
    /// </summary>
    Task NotifyCommentPostedAsync(Guid commentId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Comment edited: delta-based mentions only, the same rule as page edits
    /// (design.md §8) diffing the saved body against <paramref name="previousBody"/> —
    /// only users NOT already mentioned before the edit are notified, so touching up a
    /// comment never re-pings its standing mentions. No CommentReply here: the reply
    /// notification belongs to the reply's creation, not to each later touch-up.
    /// </summary>
    Task NotifyCommentEditedAsync(Guid commentId, Guid actorUserId, string previousBody, CancellationToken cancellationToken);
}

public sealed class NotificationDispatcher(
    RocketWikiDbContext db,
    IRealtimeConnectionRegistry registry,
    IHubContext<NotificationsHub> hubContext,
    string localInstanceId) : INotificationDispatcher
{
    public async Task NotifyPageChangedAsync(Guid pageId, Guid actorUserId, NotificationType type, CancellationToken cancellationToken)
    {
        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        // Watchers of the page or its whole space, never the actor themselves.
        var watcherIds = await db.Watches.AsNoTracking()
            .Where(w => (w.PageId == pageId || w.SpaceId == page.SpaceId) && w.UserId != actorUserId)
            .Select(w => w.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // New mentions only: everything in the saved content minus whatever the
        // previous revision already mentioned (all of them, for a first revision).
        // Same delta rule as comment edits - MentionParser.ExtractNewlyMentionedUserIds
        // is the one implementation of it.
        var mentionedIds = MentionParser.ExtractMentionedUserIds(page.CurrentContent).ToHashSet();
        if (mentionedIds.Count > 0 && page.CurrentRevisionNumber > 1)
        {
            var previousContent = await db.PageRevisions.AsNoTracking()
                .Where(r => r.PageId == pageId && r.RevisionNumber == page.CurrentRevisionNumber - 1)
                .Select(r => r.Content)
                .FirstOrDefaultAsync(cancellationToken);
            mentionedIds = MentionParser.ExtractNewlyMentionedUserIds(page.CurrentContent, previousContent);
        }

        mentionedIds.Remove(actorUserId);

        // Mention wins for a user who is both: one event, one notification, the more
        // specific type.
        var recipients = watcherIds.ToDictionary(id => id, _ => type);
        foreach (var mentionedId in mentionedIds)
        {
            recipients[mentionedId] = NotificationType.Mention;
        }

        await FanOutAsync(page, recipients, actorUserId, cancellationToken);
    }

    public async Task NotifyCommentPostedAsync(Guid commentId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return;
        }

        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == comment.PageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        var recipients = new Dictionary<Guid, NotificationType>();

        // A reply notifies the parent comment's author (design.md §8: "someone replied
        // to your comment") - never the actor replying to themselves. A tombstoned
        // parent still has its author and still gets the nudge: replies to deleted
        // comments are legal (the thread shape survives deletion, data-model.md), and
        // canView at fan-out below is the only gate that matters for what it reveals.
        if (comment.ParentCommentId is { } parentCommentId)
        {
            var parentAuthorId = await db.Comments.AsNoTracking()
                .Where(c => c.Id == parentCommentId)
                .Select(c => (Guid?)c.AuthorUserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (parentAuthorId is { } authorId && authorId != actorUserId)
            {
                recipients[authorId] = NotificationType.CommentReply;
            }
        }

        var mentionedIds = MentionParser.ExtractMentionedUserIds(comment.Body).ToHashSet();
        mentionedIds.Remove(actorUserId);

        // Mention beats reply for a parent author who is also mentioned - one event,
        // one notification, the most specific type, mirroring mention-beats-watch above.
        foreach (var mentionedId in mentionedIds)
        {
            recipients[mentionedId] = NotificationType.Mention;
        }

        await FanOutAsync(page, recipients, actorUserId, cancellationToken);
    }

    public async Task NotifyCommentEditedAsync(Guid commentId, Guid actorUserId, string previousBody, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return;
        }

        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == comment.PageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        // Delta only (design.md §8, same rule as page edits): the saved body against
        // the pre-edit body the service captured - re-saving a comment never re-pings
        // users it already mentioned.
        var newlyMentionedIds = MentionParser.ExtractNewlyMentionedUserIds(comment.Body, previousBody);
        newlyMentionedIds.Remove(actorUserId);

        var recipients = newlyMentionedIds.ToDictionary(id => id, _ => NotificationType.Mention);
        await FanOutAsync(page, recipients, actorUserId, cancellationToken);
    }

    private async Task FanOutAsync(
        Page page, Dictionary<Guid, NotificationType> recipients, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        // design.md §15: a span here because fan-out is several queries plus a canView
        // evaluation and a push per candidate - a unit of work SQL Client's per-query
        // spans can't show as one thing. Tagged with the page id (an identifier, allowed
        // by §15) and counts; never TitleSnapshot, which is page content, and never a
        // recipient id, which would make this trace a record of who can see what.
        using var activity = ApiTelemetry.ActivitySource.StartActivity(
            ApiTelemetry.NotificationFanOutSpan, ActivityKind.Internal);
        activity?.SetTag(DataTelemetry.PageIdTag, page.Id);

        var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorUserId, cancellationToken);

        var spaceGrants = await db.AccessRules.AsNoTracking()
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == page.SpaceId)
            .ToListAsync(cancellationToken);
        var ancestorIds = page.GetAncestorIds();
        var restrictions = await db.AccessRules.AsNoTracking()
            .Where(r => r.Kind == AccessRuleKind.PageRestriction &&
                r.PageId != null && (r.PageId == page.Id || ancestorIds.Contains(r.PageId.Value)))
            .ToListAsync(cancellationToken);
        var isReplica = space is not null && space.IsReplicaOf(localInstanceId);

        // Split the (already precedence-resolved) candidate set by connectivity. The
        // split happens AFTER the recipients dictionary is final, so mention-beats-watch
        // and mention-beats-reply hold across the whole set: a user is one entry with
        // one winning type, whichever half they land in - one row per user per event.
        var connected = new List<(Guid RecipientId, NotificationType Type, Principal Principal)>();
        var offlineCandidateIds = new List<Guid>();
        foreach (var (recipientId, recipientType) in recipients)
        {
            if (registry.GetConnectedPrincipal(recipientId) is { } principal)
            {
                connected.Add((recipientId, recipientType, principal));
            }
            else
            {
                offlineCandidateIds.Add(recipientId);
            }
        }

        // Mentioned ids come from user-typed Markdown, so an offline candidate may name
        // no local User row at all - such an id must simply produce nothing, not a
        // Notification FK violation that fails the whole mutation. Connected recipients
        // are inherently real users (a live connection implies a provisioned row).
        var offlineRecipientIds = offlineCandidateIds.Count == 0
            ? new List<Guid>()
            : await db.Users.AsNoTracking()
                .Where(u => offlineCandidateIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var toPush = new List<(Guid RecipientId, Notification Row)>();
        var deferredOffline = new Dictionary<NotificationType, int>();
        var skippedNotViewable = new Dictionary<NotificationType, int>();

        foreach (var (recipientId, recipientType, principal) in connected)
        {
            var permission = EffectivePermissionCalculator.Compute(spaceGrants, restrictions, isReplica, principal);
            if (!permission.CanView)
            {
                Bump(skippedNotViewable, recipientType);
                continue;
            }

            var row = new Notification
            {
                RecipientUserId = recipientId,
                Type = recipientType,
                PageId = page.Id,
                SpaceId = page.SpaceId,
                ActorUserId = actorUserId,
                TitleSnapshot = page.Title,
                CreatedAtUtc = now,
            };
            db.Notifications.Add(row);
            toPush.Add((recipientId, row));
        }

        foreach (var recipientId in offlineRecipientIds)
        {
            // Deferred row (design.md §8, same pattern as sync-imported rows): no live
            // token exists for an offline recipient, so no canView ran and NO
            // DISCLOSURE occurs here - TitleSnapshot stays null precisely because
            // there was no principal to authorize one. Existence and title are both
            // resolved at fetch by NotificationReadModelService.SurvivesReadTimeCheck
            // against the recipient's live token-built Principal, which answers the
            // two fail-closed questions this row raises:
            //  - offline AND cannot view at fetch: the recipient never learns the row
            //    existed (suppressed entirely - same as sync rows);
            //  - offline, could have viewed at send time, but lost access before
            //    fetching: also suppressed at fetch. Correct under this pattern -
            //    no disclosure ever occurred at send time, so unlike a snapshot row
            //    there is nothing the recipient "legitimately learned" to preserve.
            db.Notifications.Add(new Notification
            {
                RecipientUserId = recipientId,
                Type = recipients[recipientId],
                PageId = page.Id,
                SpaceId = page.SpaceId,
                ActorUserId = actorUserId,
                TitleSnapshot = null,
                CreatedAtUtc = now,
            });
            Bump(deferredOffline, recipients[recipientId]);
        }

        if (toPush.Count > 0 || offlineRecipientIds.Count > 0)
        {
            // Persist BEFORE pushing: the live payload carries the row's id, so the
            // client can de-duplicate a push against a later `notifications` refetch of
            // the same row (web/src/notifications/useNotifications.ts does exactly that).
            await db.SaveChangesAsync(cancellationToken);

            foreach (var (recipientId, row) in toPush)
            {
                // Field names and the snake_case type vocabulary are the shipped
                // frontend contract (web/src/realtime/types.ts NotificationPayload).
                await hubContext.Clients.Group(NotificationsHub.UserGroupName(recipientId)).SendAsync("Notification", new
                {
                    id = row.Id.ToString(),
                    type = NotificationWireFormat.TypeString(row.Type),
                    pageId = page.Id,
                    spaceKey = space?.Key,
                    pageTitle = row.TitleSnapshot,
                    actorDisplayName = actor?.DisplayName ?? "System",
                    timestampUtc = now,
                    readAtUtc = (DateTime?)null,
                }, cancellationToken);
            }
        }

        // Recorded after the save, so both row-writing dispositions count rows that
        // actually committed (design.md §15 - see RocketWikiDbContext.PendingTelemetry
        // for the same rule). Three dispositions, a bounded vocabulary (§15), one per
        // candidate: delivered_live (send-time canView passed, row + push),
        // deferred_offline (no live principal, blind row gated at fetch), and
        // skipped_not_viewable (send-time canView failed, nothing persisted).
        foreach (var group in toPush.GroupBy(p => p.Row.Type))
        {
            ApiTelemetry.RecordNotificationFanOut(group.Key, ApiTelemetry.NotificationDeliveredLive, group.Count());
        }

        foreach (var (deferredType, count) in deferredOffline)
        {
            ApiTelemetry.RecordNotificationFanOut(deferredType, ApiTelemetry.NotificationDeferredOffline, count);
        }

        foreach (var (skippedType, count) in skippedNotViewable)
        {
            ApiTelemetry.RecordNotificationFanOut(skippedType, ApiTelemetry.NotificationSkippedNotViewable, count);
        }

        activity?.SetTag(ApiTelemetry.NotificationCandidateCountTag, recipients.Count);
        activity?.SetTag(ApiTelemetry.NotificationDeliveredCountTag, toPush.Count);
        activity?.SetTag(ApiTelemetry.NotificationDeferredCountTag, offlineRecipientIds.Count);
    }

    private static void Bump(Dictionary<NotificationType, int> counts, NotificationType type) =>
        counts[type] = counts.TryGetValue(type, out var current) ? current + 1 : 1;
}
