using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Access;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// design.md §8's per-user fan-out for durable notifications: compute candidate
/// recipients (watchers of the page or its space, plus users newly mentioned in the
/// saved Markdown, minus the actor), evaluate canView per recipient AT SEND TIME, and
/// persist a Notification row plus push live only to those who pass. Mentions are
/// parsed from the <c>@[display](user://{id})</c> Markdown form (§4, MentionParser);
/// on a page edit only mentions ABSENT from the previous revision notify, so re-saving
/// a page doesn't re-ping everyone already mentioned in it. A user who is both a
/// watcher and newly mentioned gets one notification, the more specific Mention.
///
/// **Known, deliberately scoped gap.** canView requires a live ABAC <see cref="Principal"/>
/// built from a validated token (design.md §6.1), and there is no substitute for an
/// offline recipient's token: the local <c>User</c> row is explicitly not a valid
/// stand-in for an authorization decision anywhere else in this system (§6.1's whole
/// point), and manufacturing one here would be the same anti-pattern with extra steps.
/// So only recipients with a currently-open SignalR connection — and therefore a live
/// registered Principal via <see cref="IRealtimeConnectionRegistry"/> — are evaluated
/// and notified; offline watchers get neither a persisted row nor a push from this
/// implementation. Design.md's "notifications are also persisted so users who were
/// offline catch up" is therefore not yet fully realized here — flagged to the team,
/// not silently dropped. Closing it needs either a deliberate policy decision about
/// what "authorization at send time" can mean for someone with no live token, or a
/// deferred-evaluation design (re-check at next login instead of at send time).
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>Page saved (create, edit, revision restore): notifies watchers with <paramref name="type"/> and newly-mentioned users with <see cref="NotificationType.Mention"/>.</summary>
    Task NotifyPageChangedAsync(Guid pageId, Guid actorUserId, NotificationType type, CancellationToken cancellationToken);

    /// <summary>Comment added: notifies users mentioned in the comment body (design.md §8/§4). Reply notifications (CommentReply) are a documented follow-up, not built this round.</summary>
    Task NotifyCommentPostedAsync(Guid commentId, Guid actorUserId, CancellationToken cancellationToken);
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
        var mentionedIds = MentionParser.ExtractMentionedUserIds(page.CurrentContent).ToHashSet();
        if (mentionedIds.Count > 0 && page.CurrentRevisionNumber > 1)
        {
            var previousContent = await db.PageRevisions.AsNoTracking()
                .Where(r => r.PageId == pageId && r.RevisionNumber == page.CurrentRevisionNumber - 1)
                .Select(r => r.Content)
                .FirstOrDefaultAsync(cancellationToken);
            mentionedIds.ExceptWith(MentionParser.ExtractMentionedUserIds(previousContent));
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

        var mentionedIds = MentionParser.ExtractMentionedUserIds(comment.Body).ToHashSet();
        mentionedIds.Remove(actorUserId);

        var recipients = mentionedIds.ToDictionary(id => id, _ => NotificationType.Mention);
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
        activity?.SetTag("rocketwiki.page.id", page.Id);

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

        var now = DateTime.UtcNow;
        var toPush = new List<(Guid RecipientId, Notification Row)>();
        var skippedOffline = new Dictionary<NotificationType, int>();
        var skippedNotViewable = new Dictionary<NotificationType, int>();
        foreach (var (recipientId, recipientType) in recipients)
        {
            var principal = registry.GetConnectedPrincipal(recipientId);
            if (principal is null)
            {
                Bump(skippedOffline, recipientType);
                continue; // offline recipient - see this interface's own doc for the gap
            }

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

        if (toPush.Count > 0)
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

        // Recorded after the save, so `delivered` counts rows that actually committed
        // (design.md §15 - see RocketWikiDbContext.PendingTelemetry for the same rule).
        // The two skip counters are the operational measure of this dispatcher's two
        // documented behaviours: the offline-recipient gap, and canView at send time.
        foreach (var group in toPush.GroupBy(p => p.Row.Type))
        {
            ApiTelemetry.RecordNotificationFanOut(group.Key, ApiTelemetry.NotificationDelivered, group.Count());
        }

        foreach (var (skippedType, count) in skippedOffline)
        {
            ApiTelemetry.RecordNotificationFanOut(skippedType, ApiTelemetry.NotificationSkippedOffline, count);
        }

        foreach (var (skippedType, count) in skippedNotViewable)
        {
            ApiTelemetry.RecordNotificationFanOut(skippedType, ApiTelemetry.NotificationSkippedNotViewable, count);
        }

        activity?.SetTag("rocketwiki.notification.candidate_count", recipients.Count);
        activity?.SetTag("rocketwiki.notification.delivered_count", toPush.Count);
    }

    private static void Bump(Dictionary<NotificationType, int> counts, NotificationType type) =>
        counts[type] = counts.TryGetValue(type, out var current) ? current + 1 : 1;
}
