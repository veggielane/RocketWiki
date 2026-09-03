using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of <see cref="INotificationReadModelService"/>. The list
/// side re-checks canView per row against the caller's live token-built Principal
/// (data-model.md: "re-check canView when rendering the list anyway... stale titles
/// must not resurface") — batched the same way PageService's subtree checks are: one
/// grants query, one restrictions query over every involved page's ancestors+self,
/// then pure in-memory evaluation per row. For rows written without a send-time
/// canView — marked by a null TitleSnapshot: sync-imported rows from the offline CLI,
/// and deferred rows the dispatcher writes for offline recipients — the read-time
/// check is not a re-check but THE check — see <see cref="SurvivesReadTimeCheck"/>.
/// </summary>
public class NotificationReadModelService : INotificationReadModelService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;

    /// <summary>No-tracking, like every other query in this read model.</summary>
    private readonly PermissionContextLoader _permissions;

    public NotificationReadModelService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db, noTracking: true);
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

        // Two queries for every page-scoped row's canView, however many rows came back.
        // Space-scoped existence-gated rows (null TitleSnapshot, no page id - the
        // space-scoped SyncImported shape) need their space's grants too (for the
        // any-space-role check below), not only the spaces of page-scoped rows: hence
        // the extra space ids, still inside the same grants query.
        var extraSpaceIds = rows
            .Where(n => n.TitleSnapshot == null && n.SpaceId != null)
            .Select(n => n.SpaceId!.Value)
            .Distinct().ToArray();
        var permissions = await _permissions.LoadBatchAsync(
            pages.Values.Select(PermissionSubject.For).ToList(), extraSpaceIds, cancellationToken);

        var viewablePageIds = new HashSet<Guid>();
        foreach (var page in pages.Values)
        {
            if (!spaces.TryGetValue(page.SpaceId, out var space))
            {
                continue; // fail closed: no resolvable space, no title
            }

            var permission = permissions
                .For(PermissionSubject.For(page), space.IsReplicaOf(_localInstanceId))
                .Compute(principal);
            if (permission.CanView)
            {
                viewablePageIds.Add(page.Id);
            }
        }

        return rows
            .Where(n => SurvivesReadTimeCheck(n, viewablePageIds, permissions, principal))
            .Select(n => ToListItem(n, viewablePageIds, spaces, actors, pages))
            .ToList();
    }

    /// <summary>
    /// Read-time gate on a row's EXISTENCE, not just its title. The discriminator is
    /// the TitleSnapshot, not the row's type: a snapshot is only ever written after a
    /// send-time canView against the recipient's live Principal
    /// (NotificationDispatcher, connected recipients), so a row carrying one always
    /// survives - the recipient legitimately learned of the event then, and only the
    /// stale title is withheld (ToListItem). A null TitleSnapshot means NO send-time
    /// authorization ever ran, in either of the two ways that happens: the row was
    /// written by the offline sync CLI (SyncImported, see BundleImportService), or by
    /// the dispatcher for a recipient with no live connection (deferred
    /// watch/mention/reply rows). For those, the check the row never got happens HERE,
    /// against the caller's live token-built Principal - page rows need canView on the
    /// page now, space rows the same any-space-role gate that watching the space
    /// required (WatchService). Fail closed: an unresolvable subject (deleted page,
    /// missing grants, malformed row) suppresses the row entirely - a notification
    /// must never be the way someone without access learns a restricted page exists,
    /// is active, or mentions them. That answers both deferred-row questions the same
    /// way: a recipient who was offline and cannot view never learns the row existed,
    /// and one who could view at send time but lost access before fetching has the row
    /// suppressed too - nothing was disclosed at send time, so there is nothing
    /// legitimately learned to preserve.
    /// </summary>
    private static bool SurvivesReadTimeCheck(
        Notification row, HashSet<Guid> viewablePageIds, PermissionContextBatch permissions, Principal principal)
    {
        if (row.TitleSnapshot is not null)
        {
            return true;
        }

        if (row.PageId is { } pageId)
        {
            return viewablePageIds.Contains(pageId);
        }

        // A space with no grants yields an empty list here, and no role, which denies.
        return row.SpaceId is { } spaceId
            && EffectivePermissionCalculator.HasSpaceAccess(permissions.GrantsFor(spaceId), principal);
    }

    public async Task<PageMutationResult<NotificationListItem>> MarkNotificationReadAsync(
        long notificationId, Guid actingUserId, Principal principal, AuditContext auditContext, CancellationToken cancellationToken = default)
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

        // Existence-gated rows (null TitleSnapshot - see SurvivesReadTimeCheck) are
        // gated here too, against the same live Principal the list uses. Withholding
        // subject fields alone would not be airtight for the dispatcher's deferred
        // rows: the bare receipt (type + actor + timestamp) already tells a probing
        // recipient without canView that, say, someone mentioned them on a page they
        // cannot see - exactly the existence disclosure the list suppresses. So a
        // gated row that fails the check is NotFound, indistinguishable from a row
        // that does not exist, and is NOT marked read: if access is later restored,
        // it surfaces unread. (The failure is deliberately a miss, not a recorded
        // denial, for the same reason the owner-scope miss above is - to this caller
        // the row does not exist.)
        if (notification.TitleSnapshot is null
            && !await GatedRowSurvivesAsync(notification, principal, cancellationToken))
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

        // The mark-read response is a receipt, not a disclosure surface: the title is
        // always omitted (the shipped MarkNotificationRead operation selects only
        // { id readAtUtc }), and for existence-gated rows the page id and space key
        // are withheld too. The gate above spent its canView on the row's EXISTENCE
        // only; the list is where subjects are rendered, with the full batched
        // re-check. Keeping the receipt subject-free means the answer to a mark-read
        // is never richer than "your row, now read".
        var withholdSubject = notification.TitleSnapshot is null;
        var spaceKey = !withholdSubject && notification.SpaceId is { } spaceId
            ? await _db.Spaces.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.Id == spaceId).Select(s => s.Key).FirstOrDefaultAsync(cancellationToken)
            : null;
        var actorDisplayName = notification.ActorUserId is { } actorId
            ? await _db.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken)
            : null;

        return PageMutationResult<NotificationListItem>.Success(new NotificationListItem(
            notification.Id,
            notification.Type,
            withholdSubject ? null : notification.PageId,
            spaceKey,
            PageTitle: null,
            actorDisplayName ?? "System",
            notification.CreatedAtUtc,
            notification.ReadAtUtc));
    }

    /// <summary>
    /// Single-row form of <see cref="SurvivesReadTimeCheck"/> for the mark-read path,
    /// same rules, fetched fresh: page rows require canView on the page NOW (page
    /// resolved through the normal query filter, so a soft-deleted page fails closed;
    /// space resolved ignoring filters, same as the list), space-scoped rows the same
    /// any-space-role gate watching required. A row with no subject at all is
    /// malformed and fails closed.
    /// </summary>
    private async Task<bool> GatedRowSurvivesAsync(Notification row, Principal principal, CancellationToken cancellationToken)
    {
        if (row.PageId is { } pageId)
        {
            var page = await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
            if (page is null)
            {
                return false;
            }

            var space = await _db.Spaces.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
            if (space is null)
            {
                return false;
            }

            var context = await _permissions.LoadAsync(page, space.IsReplicaOf(_localInstanceId), cancellationToken);
            return context.Compute(principal).CanView;
        }

        if (row.SpaceId is { } spaceId)
        {
            var grants = await _permissions.LoadSpaceGrantsAsync(spaceId, cancellationToken);
            return grants.Count > 0 && EffectivePermissionCalculator.HasSpaceAccess(grants, principal);
        }

        return false;
    }

    private static NotificationListItem ToListItem(
        Notification row,
        HashSet<Guid> viewablePageIds,
        Dictionary<Guid, Space> spaces,
        Dictionary<Guid, User> actors,
        Dictionary<Guid, Page> pages)
    {
        var canViewPage = row.PageId is { } pageId && viewablePageIds.Contains(pageId);

        // TitleSnapshot was already "as permitted at send time" (data-model.md);
        // surfacing it still requires canView to hold NOW. A null-snapshot row has
        // nothing attested at write time (the offline sync CLI, or the dispatcher's
        // deferred rows for offline recipients), so its title is the page's LIVE title
        // instead: such a row only survived SurvivesReadTimeCheck because the caller's
        // live Principal passes canView on that page right now, and a title the caller
        // can open the page to read is not a disclosure.
        var title = row.TitleSnapshot is null
            ? (canViewPage && pages.TryGetValue(row.PageId!.Value, out var page) ? page.Title : null)
            : (canViewPage ? row.TitleSnapshot : null);

        return new NotificationListItem(
            row.Id,
            row.Type,
            row.PageId,
            row.SpaceId is { } spaceId && spaces.TryGetValue(spaceId, out var space) ? space.Key : null,
            title,
            row.ActorUserId is { } actorId && actors.TryGetValue(actorId, out var actor) ? actor.DisplayName : "System",
            row.CreatedAtUtc,
            row.ReadAtUtc);
    }
}
