using System.Text.Json;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Audit;

/// <summary>
/// The one place a denied *read* becomes an audit row (design.md §6.7/§7). Read
/// services return the internal ReadResult/AttachmentDownloadResult.Denied with the
/// failing-restriction reason the rule engine computed; resolvers and routes call this
/// before collapsing the result to the same null/empty/404 a NotFound gets. This
/// mirrors the mutation side's split exactly (MutationAuthHelper.AuditDenialIfApplicableAsync,
/// AuditFieldMiddleware's doc): denials are audited explicitly where the typed reason
/// is in hand, successes via AuditFieldMiddleware / the domain-event pipeline.
///
/// Design call, stated once: the API layer audits, the read service only *reports*.
/// design.md §6.7 specifies the service returns the internal result and "the resolver
/// collapses both to null at the boundary while auditing the denial" - and practically,
/// IAuditSink and its request-scoped dedup live in this project (Core/Data can't
/// reference them without a cycle), and the channel/request context the row needs is
/// derived from HttpContext here, not available inside Core.
///
/// The reason string (<c>restriction:{pageId}:{ruleId}</c> or <c>no-space-role</c>)
/// goes into the audit row's DetailsJson and nowhere else: per design.md §15 the audit
/// log keeps the specific reason while telemetry only ever carries the bounded
/// category, which EffectivePermissionCalculator's own counter already emits at the
/// point the check runs. Nothing in this class touches a metric or a span.
/// </summary>
public static class ReadDenialAudit
{
    /// <summary>A genuinely-missing subject (ReadResult.NotFound) is deliberately NOT
    /// routed here and audits nothing: §7's outcome vocabulary is success|denied, and a
    /// read of something that doesn't exist made no access decision to record - logging
    /// it as Denied would pollute the probing signal the permission inspector feeds on
    /// with plain 404 noise, and as Success would claim a read that never happened.</summary>
    public static Task RecordAsync(
        IAuditSink sink,
        string action,
        AuditSubjectType subjectType,
        Guid subjectId,
        string reason,
        CancellationToken ct)
    {
        // Same {"reason": ...} details shape the mutation-denial path writes for
        // ForbiddenError, so the audit log viewer reads one format for all denials.
        var detailsJson = JsonSerializer.Serialize(new { reason });
        return sink.RecordAsync(
            new AuditRecord(action, AuditOutcome.Denied, subjectType, subjectId, DetailsJson: detailsJson), ct);
    }
}
