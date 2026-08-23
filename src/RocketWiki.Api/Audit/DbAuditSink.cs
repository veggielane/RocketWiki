using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;
using RocketWiki.Data;

namespace RocketWiki.Api.Audit;

/// <summary>
/// Writes read/denial audit events directly to <see cref="RocketWikiDbContext.AuditEvents"/>
/// (design.md §7). Unlike a mutation's audit row, there is no domain event and no
/// <c>RaiseDomainEvent</c> call here — a read isn't a domain fact, so this inserts the
/// <see cref="AuditEvent"/> row itself and saves immediately.
///
/// Deduplicates within one request (design.md §8: "resolving Page.content or
/// Page.revisions emits page.view, deduplicated per request... a query fetching one page
/// through three paths is one view, not three"). Scoped per HTTP request, so the
/// dedup set naturally resets between requests. Keyed on (Action, SubjectId, Outcome),
/// with one deliberate asymmetry: a Success is skipped when the same (Action, SubjectId)
/// was already recorded as Denied this request. That combination is real since denied
/// reads became auditable (design.md §6.7): a resolver audits the Denied and then
/// collapses it to an empty-but-non-null shape (pageTree's [], revisions' []), which
/// AuditFieldMiddleware would otherwise dutifully record as a Success for the very
/// subject that was just refused — a denial must never also claim success. The reverse
/// order (Success then Denied, same subject) is left alone: two genuine, contradictory
/// decisions in one request means a rule changed mid-flight, and both rows are the
/// honest record of that.
/// </summary>
public sealed class DbAuditSink(
    RocketWikiDbContext db, IActingUserAccessor actingUser, ICurrentAuditContextAccessor auditContextAccessor) : IAuditSink
{
    private readonly HashSet<(string Action, Guid? SubjectId, AuditOutcome Outcome)> _recordedThisRequest = [];

    public async Task RecordAsync(AuditRecord record, CancellationToken ct)
    {
        if (record.Outcome == AuditOutcome.Success
            && _recordedThisRequest.Contains((record.Action, record.SubjectId, AuditOutcome.Denied)))
        {
            return;
        }

        var dedupKey = (record.Action, record.SubjectId, record.Outcome);
        if (!_recordedThisRequest.Add(dedupKey))
        {
            return;
        }

        // Fail closed, extended to auditing itself: AuditEvent.UserId being null is
        // reserved for genuine system actors (the sync CLI). A read or denial with no
        // resolvable identity must not produce a row that looks like one of those —
        // design.md's "no anonymous wikis" means this should never legitimately happen
        // for a real request, so treat it as a bug to surface loudly, not paper over.
        if (actingUser.ActingUserId is null)
        {
            throw new InvalidOperationException(
                "Cannot record an audit event for a request with no resolvable acting user " +
                "(design.md §7). Writing UserId = null here would be indistinguishable from " +
                "a genuine system-actor event.");
        }

        var auditContext = auditContextAccessor.Current
            ?? throw new InvalidOperationException(
                "No AuditContext could be derived for this request (design.md §7) - the request " +
                "path doesn't map to a known channel. An audited read/denial should never reach " +
                "here from a path CurrentAuditContextAccessor doesn't recognize.");

        db.AuditEvents.Add(new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = actingUser.ActingUserId,
            Action = record.Action,
            SubjectType = record.SubjectType,
            SubjectId = record.SubjectId,
            SpaceKey = record.SpaceKey,
            Outcome = record.Outcome,
            Channel = auditContext.Channel,
            RequestId = auditContext.RequestId,
            ClientIp = auditContext.ClientIp,
            McpClient = auditContext.McpClient,
            DetailsJson = record.DetailsJson,
        });

        await db.SaveChangesAsync(ct);

        // After the save, never before: design.md §7 requires a failed audit insert to
        // fail the request, so a counter incremented on the way in would report rows
        // that were never written. record.Action is a declared [AuditAction] name and
        // the other two are enums - a bounded vocabulary, never the subject's content
        // (§15). record.DetailsJson is deliberately not tagged: it is the audit row's
        // payload, and the audit table is where it belongs.
        CoreTelemetry.RecordAuditEventWritten(
            record.Action, record.Outcome, auditContext.Channel, CoreTelemetry.AuditWriterSink);
    }
}
