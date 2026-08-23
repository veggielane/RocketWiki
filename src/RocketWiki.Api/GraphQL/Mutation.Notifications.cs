using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>Thin GraphQL layer over <see cref="INotificationReadModelService"/> — see
/// Mutation.cs's class doc for the shared plumbing. Field, input, and payload names
/// match the shipped frontend operation exactly
/// (web/src/graphql/operations/notifications.graphql: <c>markNotificationRead</c>,
/// <c>MarkNotificationReadInput.notificationId</c>, payload's <c>notification</c>).</summary>
public partial class Mutation
{
    [AuditAction("notification.markRead")]
    public async Task<MarkNotificationReadPayload> MarkNotificationRead(
        MarkNotificationReadInput input,
        [Service] INotificationReadModelService notificationService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new MarkNotificationReadPayload(null, unauthenticated);
        }

        if (!long.TryParse(input.NotificationId, out var notificationId))
        {
            return new MarkNotificationReadPayload(null, new PageMutationErrorView(
                "Validation", "notificationId must be a notification row id.", null, null, null, null, null, null, null));
        }

        var result = await notificationService.MarkNotificationReadAsync(notificationId, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            // No denial audit call here, deliberately: this path can only fail
            // NotFound/Validation (the service is owner-scoped by construction, so
            // there is no permission-shaped failure to record - design.md §7 audits
            // denials, not misses).
            return new MarkNotificationReadPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new MarkNotificationReadPayload(NotificationView.From(result.Value), null);
    }
}

/// <summary>Named to match the shipped operation's variable type (<c>MarkNotificationReadInput</c>),
/// which is why this input lives here rather than following the Core-side <c>*Request</c>
/// convention: the frontend contract predates this resolver. <c>notificationId</c> is the
/// string form of <c>Notification.id</c>, exactly as the list returned it.</summary>
public sealed record MarkNotificationReadInput(string NotificationId);

public sealed record MarkNotificationReadPayload(NotificationView? Notification, PageMutationErrorView? Error);
