using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>design.md §8 schema sketch: "auditEvents(filter, after): AuditEventConnection! # admin".</summary>
public sealed record AuditFilterInput(
    Guid? UserId,
    string? Action,
    AuditSubjectType? SubjectType,
    Guid? SubjectId,
    AuditOutcome? Outcome,
    DateTime? FromUtc,
    DateTime? ToUtc);

public partial class Query
{
    /// <summary>
    /// design.md §7/§8: "audit log viewer for instance admins: filter by user, action,
    /// subject, outcome, and date range... viewing the audit log is itself audited
    /// (audit.view)." Unlike every read elsewhere in this schema, a non-admin caller
    /// gets an explicit GraphQL error rather than an absent/empty result — there is no
    /// existence-leak risk here the way there is for a Page (everyone already knows an
    /// audit log exists), so an honest "you can't do this" is the more useful signal for
    /// what is unambiguously an admin-only screen, and design.md §7 requires the denial
    /// itself be recorded regardless.
    ///
    /// Denied events are returned alongside successes whenever the caller doesn't filter
    /// by outcome — "probing at restricted content is a signal, not noise to filter out"
    /// per team direction, so no default Outcome filter is applied.
    /// </summary>
    [AuditAction("audit.view")]
    [UseAuditDispatch]
    [UsePaging]
    public async Task<IQueryable<AuditEvent>> AuditEvents(
        AuditFilterInput filter,
        [Service] RocketWikiDbContext db,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            await auditSink.RecordAsync(new AuditRecord("audit.view", AuditOutcome.Denied), cancellationToken);
            throw new GraphQLException("Instance admin required to view the audit log.");
        }

        var query = db.AuditEvents.AsNoTracking().AsQueryable();

        if (filter.UserId is not null)
        {
            query = query.Where(e => e.UserId == filter.UserId);
        }

        if (!string.IsNullOrEmpty(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (filter.SubjectType is not null)
        {
            query = query.Where(e => e.SubjectType == filter.SubjectType);
        }

        if (filter.SubjectId is not null)
        {
            query = query.Where(e => e.SubjectId == filter.SubjectId);
        }

        if (filter.Outcome is not null)
        {
            query = query.Where(e => e.Outcome == filter.Outcome);
        }

        if (filter.FromUtc is not null)
        {
            query = query.Where(e => e.TimestampUtc >= filter.FromUtc);
        }

        if (filter.ToUtc is not null)
        {
            query = query.Where(e => e.TimestampUtc <= filter.ToUtc);
        }

        return query.OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id);
    }
}
