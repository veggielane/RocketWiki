using System.Text.Json;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Shared across every mutation type over the *Service (Page/Comment/Label/Attachment)
/// pattern in RocketWiki.Core.Services — they all take the same
/// (Principal, actingUserId, AuditContext) shape and return the same
/// <see cref="PageMutationError"/> hierarchy, so the "refuse before calling the service"
/// and "audit a denial explicitly" logic only needs to exist once.
/// </summary>
public static class MutationAuthHelper
{
    /// <summary>
    /// Refuses before the service is ever called if there's no resolvable Principal,
    /// actingUserId, or AuditContext (design.md: no anonymous wikis; a mutation with
    /// nowhere to route its audit record must not proceed - the same principle
    /// MissingAuditContextException enforces one layer down, applied here before the
    /// service is even called). Deliberately unaudited: DbAuditSink itself refuses to
    /// record anything for an unresolvable acting user (fail-closed extended to
    /// auditing, see its own doc) - there is no user to attribute this attempt to,
    /// and a well-behaved client never reaches this path anyway.
    /// </summary>
    public static (Principal? Principal, Guid? ActingUserId, AuditContext? AuditContext, PageMutationErrorView? Unauthenticated) Authenticate(
        ICurrentPrincipalAccessor principalAccessor, IActingUserAccessor actingUserAccessor, ICurrentAuditContextAccessor auditContextAccessor)
    {
        var principal = principalAccessor.Current;
        var actingUserId = actingUserAccessor.ActingUserId;
        var auditContext = auditContextAccessor.Current;

        if (principal is null || actingUserId is null || auditContext is null)
        {
            return (null, null, null, new PageMutationErrorView(
                "Forbidden", "Authentication required.", null, null, null, null, null, null, null));
        }

        return (principal, actingUserId, auditContext, null);
    }

    /// <summary>
    /// Only permission-shaped failures are denials worth an audit row (design.md §7)
    /// — NotFound/Validation/StaleRevision are ordinary outcomes, not access refusals,
    /// and auditing them as "Denied" would conflate "you can't" with "that doesn't
    /// exist" or "someone else saved first".
    /// </summary>
    public static Task AuditDenialIfApplicableAsync(
        IAuditSink sink, string action, PageMutationError error, AuditSubjectType subjectType, Guid? subjectId, CancellationToken ct)
    {
        var detailsJson = error switch
        {
            ForbiddenError e => JsonSerializer.Serialize(new { reason = e.Reason }),
            ReadOnlyReplicaError e => JsonSerializer.Serialize(new { spaceId = e.SpaceId }),
            SubtreeOperationForbiddenError e => JsonSerializer.Serialize(new { blockedPageCount = e.BlockedPageCount }),
            _ => null,
        };

        if (detailsJson is null)
        {
            return Task.CompletedTask;
        }

        return sink.RecordAsync(new AuditRecord(action, AuditOutcome.Denied, subjectType, subjectId, DetailsJson: detailsJson), ct);
    }
}
