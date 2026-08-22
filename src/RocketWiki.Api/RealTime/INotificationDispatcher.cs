using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// design.md §8's per-user fan-out for durable notifications: compute candidate
/// recipients (watchers of the page or its space, minus the actor), evaluate canView
/// per recipient AT SEND TIME, and persist a row plus push live only to those who pass.
/// Mentions ("parsed from user://{id} links on save") are a documented follow-up, not
/// built this round — nothing calls this with anything but a plain page-change today.
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
    Task NotifyPageChangedAsync(Guid pageId, Guid actorUserId, NotificationType type, CancellationToken cancellationToken);
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

        var candidateIds = await db.Watches.AsNoTracking()
            .Where(w => (w.PageId == pageId || w.SpaceId == page.SpaceId) && w.UserId != actorUserId)
            .Select(w => w.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (candidateIds.Count == 0)
        {
            return;
        }

        var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorUserId, cancellationToken);

        var spaceGrants = await db.AccessRules.AsNoTracking()
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == page.SpaceId)
            .ToListAsync(cancellationToken);
        var ancestorIds = page.GetAncestorIds();
        var restrictions = await db.AccessRules.AsNoTracking()
            .Where(r => r.Kind == AccessRuleKind.PageRestriction &&
                r.PageId != null && (r.PageId == pageId || ancestorIds.Contains(r.PageId.Value)))
            .ToListAsync(cancellationToken);
        var isReplica = space is not null && space.IsReplicaOf(localInstanceId);

        var now = DateTime.UtcNow;
        var delivered = 0;
        foreach (var recipientId in candidateIds)
        {
            var principal = registry.GetConnectedPrincipal(recipientId);
            if (principal is null)
            {
                continue; // offline recipient - see this interface's own doc for the gap
            }

            var permission = EffectivePermissionCalculator.Compute(spaceGrants, restrictions, isReplica, principal);
            if (!permission.CanView)
            {
                continue;
            }

            db.Notifications.Add(new Notification
            {
                RecipientUserId = recipientId,
                Type = type,
                PageId = pageId,
                SpaceId = page.SpaceId,
                ActorUserId = actorUserId,
                TitleSnapshot = page.Title,
                CreatedAtUtc = now,
            });
            delivered++;

            await hubContext.Clients.Group(NotificationsHub.UserGroupName(recipientId)).SendAsync("Notification", new
            {
                type = type.ToString(),
                pageId,
                spaceKey = space?.Key,
                actorDisplayName = actor?.DisplayName,
                title = page.Title,
                timestamp = now,
            }, cancellationToken);
        }

        if (delivered > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
