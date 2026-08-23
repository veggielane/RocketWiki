using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of <see cref="INotificationReadModelService"/>. The list
/// side re-checks canView per row against the caller's live token-built Principal
/// (data-model.md: "re-check canView when rendering the list anyway... stale titles
/// must not resurface") — batched the same way PageService's subtree checks are: one
/// grants query, one restrictions query over every involved page's ancestors+self,
/// then pure in-memory evaluation per row.
/// </summary>
public class NotificationReadModelService : INotificationReadModelService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;

    public NotificationReadModelService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
    }

    public async Task<IReadOnlyList<NotificationListItem>> GetNotificationsAsync(
        Guid recipientUserId, Principal principal, int take, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == recipientUserId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        // Pages through the normal query filter: a soft-deleted page simply fails the
        // re-check (title suppressed), same as one the recipient lost access to.
        var pageIds = rows.Where(n => n.PageId != null).Select(n => n.PageId!.Value).Distinct().ToArray();
        var pages = await _db.Pages.AsNoTracking()
            .Where(p => pageIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // Space keys via IgnoreQueryFilters: an archived space's key is not restricted
        // information for someone who was notified out of it, and the row keeps naming
        // its space either way.
        var spaceIds = rows.Where(n => n.SpaceId != null).Select(n => n.SpaceId!.Value)
            .Concat(pages.Values.Select(p => p.SpaceId))
            .Distinct().ToArray();
        var spaces = await _db.Spaces.AsNoTracking().IgnoreQueryFilters()
            .Where(s => spaceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var actorIds = rows.Where(n => n.ActorUserId != null).Select(n => n.ActorUserId!.Value).Distinct().ToArray();
        var actors = await _db.Users.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        var involvedSpaceIds = pages.Values.Select(p => p.SpaceId).Distinct().ToArray();
        var grantsBySpace = (await _db.AccessRules.AsNoTracking()
                .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId != null && involvedSpaceIds.Contains(r.SpaceId.Value))
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.SpaceId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var restrictionPageIds = pages.Values
            .SelectMany(p => p.GetAncestorIds().Append(p.Id))
            .Distinct().ToArray();
        var allRestrictions = restrictionPageIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules.AsNoTracking()
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && restrictionPageIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        var viewablePageIds = new HashSet<Guid>();
        foreach (var page in pages.Values)
        {
            if (!spaces.TryGetValue(page.SpaceId, out var space))
            {
                continue; // fail closed: no resolvable space, no title
            }

            var applicable = new HashSet<Guid>(page.GetAncestorIds()) { page.Id };
            var restrictions = allRestrictions.Where(r => applicable.Contains(r.PageId!.Value)).ToList();
            var grants = grantsBySpace.TryGetValue(page.SpaceId, out var g) ? g : new List<AccessRule>();
            var permission = EffectivePermissionCalculator.Compute(
                grants, restrictions, space.IsReplicaOf(_localInstanceId), principal);
            if (permission.CanView)
            {
                viewablePageIds.Add(page.Id);
            }
        }

        return rows.Select(n => ToListItem(n, viewablePageIds, spaces, actors)).ToList();
    }

    public async Task<PageMutationResult<NotificationListItem>> MarkNotificationReadAsync(
        long notificationId, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        // Owner-scoped by construction: another recipient's row (or a nonexistent id)
        // is the same NotFound - a notification's existence is visible to its
        // recipient only.
        var notification = await _db.Notifications.FirstOrDefaultAsync(
            n => n.Id == notificationId && n.RecipientUserId == actingUserId, cancellationToken);
        if (notification is null)
        {
            return PageMutationResult<NotificationListItem>.Failure(new NotFoundError(Guid.Empty));
        }

        if (notification.ReadAtUtc is null)
        {
            notification.ReadAtUtc = DateTime.UtcNow;

            _db.AuditContext = auditContext;
            _db.RaiseDomainEvent(new NotificationMarkedReadEvent(notification.Id, actingUserId));
            await _db.SaveChangesAsync(cancellationToken);
        }

        // The mark-read response reuses the same title discipline as the list: no
        // fresh canView evaluation is spent here, so the title is simply omitted -
        // the shipped MarkNotificationRead operation selects only { id readAtUtc }.
        var spaceKey = notification.SpaceId is { } spaceId
            ? await _db.Spaces.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.Id == spaceId).Select(s => s.Key).FirstOrDefaultAsync(cancellationToken)
            : null;
        var actorDisplayName = notification.ActorUserId is { } actorId
            ? await _db.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken)
            : null;

        return PageMutationResult<NotificationListItem>.Success(new NotificationListItem(
            notification.Id,
            notification.Type,
            notification.PageId,
            spaceKey,
            PageTitle: null,
            actorDisplayName ?? "System",
            notification.CreatedAtUtc,
            notification.ReadAtUtc));
    }

    private static NotificationListItem ToListItem(
        Notification row,
        HashSet<Guid> viewablePageIds,
        Dictionary<Guid, Space> spaces,
        Dictionary<Guid, User> actors)
    {
        var canViewPage = row.PageId is { } pageId && viewablePageIds.Contains(pageId);
        return new NotificationListItem(
            row.Id,
            row.Type,
            row.PageId,
            row.SpaceId is { } spaceId && spaces.TryGetValue(spaceId, out var space) ? space.Key : null,
            // TitleSnapshot was already "as permitted at send time" (data-model.md);
            // surfacing it still requires canView to hold NOW.
            canViewPage ? row.TitleSnapshot : null,
            row.ActorUserId is { } actorId && actors.TryGetValue(actorId, out var actor) ? actor.DisplayName : "System",
            row.CreatedAtUtc,
            row.ReadAtUtc);
    }
}
