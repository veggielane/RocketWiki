using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// design.md §8: the persisted list — "SignalR delivers the live nudge, the table
    /// is the record", fetched on load so an offline user catches up. Deliberately a
    /// plain capped list rather than a connection: the shipped frontend operation
    /// (web/src/graphql/operations/notifications.graphql) selects a flat list, and 100
    /// most-recent rows is the whole product surface (a bell dropdown), not a browsable
    /// history. Rows only ever exist for recipients who passed canView at send time;
    /// canView is STILL re-checked per row here, nulling <c>pageTitle</c> for pages
    /// since lost (data-model.md: "stale titles must not resurface") — which the
    /// frontend renders as "a page you can no longer view".
    /// </summary>
    [AuditAction("notification.list")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<NotificationView>> Notifications(
        [Service] INotificationReadModelService notificationService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        var actingUserId = actingUserAccessor.ActingUserId;
        if (principal is null || actingUserId is null)
        {
            return [];
        }

        var items = await notificationService.GetNotificationsAsync(actingUserId.Value, principal, take: 100, cancellationToken);
        return items.Select(NotificationView.From).ToList();
    }
}

/// <summary>
/// GraphQL projection of one persisted notification, field-for-field the shipped
/// frontend contract (web/src/graphql/operations/notifications.graphql /
/// web/src/realtime/types.ts NotificationPayload): <c>id</c> is the row id as a
/// string, <c>type</c> the snake_case wire vocabulary, <c>pageTitle</c> null whenever
/// the recipient's canView no longer holds. Never content, never diffs (design.md §8).
/// </summary>
[GraphQLName("Notification")]
public sealed record NotificationView(
    string Id,
    string Type,
    Guid? PageId,
    string? SpaceKey,
    string? PageTitle,
    string ActorDisplayName,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc)
{
    public static NotificationView From(NotificationListItem item) => new(
        item.Id.ToString(),
        NotificationWireFormat.TypeString(item.Type),
        item.PageId,
        item.SpaceKey,
        item.PageTitle,
        item.ActorDisplayName,
        item.CreatedAtUtc,
        item.ReadAtUtc);
}
