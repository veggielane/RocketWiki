using System.Security.Claims;
using System.Text.Json;
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
    // IncludeTotalCount: the audit viewer's pager needs the filtered total (design.md
    // §8's known-deltas list). Admin-only data with no per-row permission filter, so
    // an exact COUNT leaks nothing here - unlike search's capped totalCount (§9.1),
    // whose cap exists because ITS rows are canView-filtered. §15's cost story is
    // unchanged: the @listSize/@cost directives on this field bound the *list* the
    // slicing arguments control, and totalCount is a scalar outside sizedFields.
    [AuditAction("audit.view")]
    [UseAuditDispatch]
    // MaxPageSize 100 because AuditLogPage asks for 100, and the cap is enforced at
    // GraphQL VALIDATION — before the resolver, before the admin gate — so the default
    // 50 refused the audit log for every admin with "the maximum allowed items per page
    // were exceeded". Safe at 100 for the same reason IncludeTotalCount is safe here and
    // is not safe for search (§9.1): these rows carry no per-row canView filter, so page
    // size reveals nothing a row wouldn't. Raised to what the page actually asks for and
    // no further.
    [UsePaging(IncludeTotalCount = true, MaxPageSize = 100)]
    public async Task<IQueryable<AuditEvent>> AuditEvents(
        AuditFilterInput filter,
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            // An anonymous caller is refused identically but records nothing: there is
            // no acting user to attribute a row to, and DbAuditSink refuses UserId-less
            // rows by design (§7) — recording unconditionally made an anonymous probe
            // fail with that refusal instead of this field's own honest one. Same stance
            // Query.Page takes for an anonymous read, and the claims are read the way
            // Query.Me reads them, since "no acting user" alone would also silence the
            // authenticated-but-unresolvable case that must stay loud.
            if (claimsPrincipal.Identity?.IsAuthenticated == true)
            {
                // Same {"reason": ...} details shape as every other denial row (§7), so
                // the audit log viewer reads one format for all denials (ReadDenialAudit).
                await auditSink.RecordAsync(
                    new AuditRecord(
                        "audit.view", AuditOutcome.Denied,
                        DetailsJson: JsonSerializer.Serialize(new { reason = "instance admin required" })),
                    cancellationToken);
            }

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
