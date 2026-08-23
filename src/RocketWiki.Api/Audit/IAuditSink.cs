using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Audit;

/// <summary>
/// Where a read (or a denial) gets audited (design.md §7). Mutations get their
/// audit row from the domain-event pipeline, in the same transaction as the
/// change (RocketWikiDbContext.RaiseDomainEvent, in RocketWiki.Data) — this
/// sink exists for everything that pipeline doesn't cover: reads
/// (<c>page.view</c>, <c>search.query</c>, <c>space.browse</c>) aren't domain
/// events, and denials happen before any mutation would.
///
/// <see cref="DbAuditSink"/> is the only implementation. Channel/RequestId/
/// ClientIp come from the ambient <see cref="RocketWiki.Core.Events.AuditContext"/>
/// on the current request's <c>RocketWikiDbContext</c> (set by
/// <see cref="AuditContextMiddleware"/>), not from the caller — a resolver
/// calling this shouldn't need to know or restate which channel it's on.
///
/// Two callers feed reads through this seam today: <see cref="AuditFieldMiddleware"/>
/// (successes, from <see cref="AuditActionAttribute"/> declarations) and
/// <see cref="ReadDenialAudit"/> (denials, with the failing-restriction reason the
/// read services now return internally per design.md §6.7).
/// </summary>
public interface IAuditSink
{
    Task RecordAsync(AuditRecord record, CancellationToken ct);
}

/// <summary>
/// One audit event's domain-specific content (design.md §7's event model,
/// minus the request/channel metadata and UserId that <see cref="DbAuditSink"/>
/// fills in from ambient context, not from the caller).
/// </summary>
public sealed record AuditRecord(
    string Action,
    AuditOutcome Outcome,
    AuditSubjectType? SubjectType = null,
    Guid? SubjectId = null,
    string? SpaceKey = null,
    string? DetailsJson = null);
