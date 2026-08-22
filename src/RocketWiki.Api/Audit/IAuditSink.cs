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
/// TODO(milestone 2+): a field middleware that reads
/// <see cref="AuditActionAttribute"/> off the resolved GraphQL field and calls
/// this automatically. Not built yet — there is no field with
/// <see cref="AuditActionAttribute"/> today (<c>Query.Me</c> is
/// <see cref="NoAuditAttribute"/>) to safely prove it against without adding a
/// real content resolver, which is out of this scope (IPageService's read
/// services are mid-flight elsewhere). Until then this sink is tested directly.
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
