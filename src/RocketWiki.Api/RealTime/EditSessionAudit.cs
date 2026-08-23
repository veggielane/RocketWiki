using System.Text.Json;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;
using RocketWiki.Data;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// The audit writer for edit-session actions (design.md §7 + §8 co-editing):
/// <c>page.edit_session.joined</c> / <c>page.edit_session.left</c> on
/// <see cref="AuditChannel.Realtime"/>. A separate writer, not <c>DbAuditSink</c>, for
/// a structural reason this project has hit before (see NotificationsHub's class doc):
/// the sink's acting-user and audit-context accessors are IHttpContextAccessor-backed,
/// which is not reliably populated inside a hub method invocation — so the hub resolves
/// user and connection context itself and this class only turns them into the row.
/// Request context maps naturally: RequestId := the SignalR ConnectionId (the
/// connection is the request on this channel), ClientIp := captured at join.
///
/// Same guarantees as every other audit path: the row is written and saved
/// synchronously, and a failed insert throws — for a join, that failure propagates
/// before any membership or group state exists, so the join fails closed (§7: "if an
/// audit insert fails, the request fails"). Leave rows on disconnect are the one
/// stated exception to fail-closed's *effect*: the insert still throws on failure,
/// but nothing can un-disconnect a socket — there is no request left to fail. Joining
/// is the authorization-consuming act, and it is the one that gates.
///
/// Presence joins remain deliberately unaudited (§8: "presence adds no new record").
/// Joining an EDIT session is different in kind: it consumes a canEdit authorization
/// and opens a channel that carries page content, so it is a §7-worthy act on content
/// — that asymmetry is the design, not an oversight.
/// </summary>
public static class EditSessionAudit
{
    public const string JoinedAction = "page.edit_session.joined";
    public const string LeftAction = "page.edit_session.left";

    public const string LeftReasonLeft = "left";
    public const string LeftReasonDisconnected = "disconnected";
    public const string LeftReasonEvicted = "evicted";

    public static async Task RecordAsync(
        RocketWikiDbContext db,
        Guid userId,
        string action,
        AuditOutcome outcome,
        Guid pageId,
        string? spaceKey,
        string? detailsJson,
        string connectionId,
        string clientIp,
        CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = userId,
            Action = action,
            SubjectType = AuditSubjectType.Page,
            SubjectId = pageId,
            SpaceKey = spaceKey,
            Outcome = outcome,
            Channel = AuditChannel.Realtime,
            RequestId = connectionId,
            ClientIp = clientIp,
            DetailsJson = detailsJson,
        });

        await db.SaveChangesAsync(cancellationToken);

        // After the save, never before (§15: a rolled-back insert must not be counted).
        CoreTelemetry.RecordAuditEventWritten(action, outcome, AuditChannel.Realtime, CoreTelemetry.AuditWriterRealtimeHub);
    }

    /// <summary>Same <c>{"reason": ...}</c> details shape as ReadDenialAudit and the
    /// mutation-denial path, so the audit viewer reads one format for all denials.</summary>
    public static string ReasonDetails(string reason) => JsonSerializer.Serialize(new { reason });
}
