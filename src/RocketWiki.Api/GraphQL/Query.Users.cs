using System.Security.Claims;
using System.Text.Json;
using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// One row of the account roster, for the instance-admin user list.
///
/// <para><b>A projection, never the <c>User</c> entity</b> — the same rule
/// <see cref="UserRef"/> states and for a sharper reason here. <c>User.AttributesJson</c>
/// mirrors registered attributes, and on this instance that means <b>nationality</b>:
/// sensitive personal data that design.md §6.2 keeps admin-visible only. Returning the
/// entity would put it on the wire the moment anyone adds a field selection, so the
/// projection is the boundary. It is deliberately absent from this type even though the
/// caller is an instance admin and could in principle be shown it: a roster screen needs
/// identity and activity, and every extra column here is one more thing an admin session
/// can leak. Add it only if someone asks for it, and audit it then.</para>
///
/// <para><b>No groups or effective permissions.</b> Those come from the
/// token (§6.1) — the rule engine never reads this table — so a list of them per user
/// would be either a lie (the values from whenever that user last signed in) or an
/// invention. What the database actually knows about a person is who they are and when
/// they were last seen, and that is what this returns.</para>
///
/// <para><see cref="HasAvatar"/> follows <see cref="UserRef"/>'s contract exactly: a
/// boolean, not a URL, so nothing treats it as a directly-embeddable <c>img src</c>
/// (§10 forbids unauthenticated blob URLs). <see cref="IsExternal"/> is included because
/// it is the honest explanation for a row with no subject that never signs in — a shadow
/// user created by sync (§12), not a dormant account an admin should chase.</para>
/// </summary>
public sealed record AdminUser(
    Guid Id,
    string? Subject,
    string DisplayName,
    string? Email,
    bool IsExternal,
    bool HasAvatar,
    DateTime CreatedAtUtc,
    DateTime LastSeenAtUtc);

public partial class Query
{
    /// <summary>
    /// The account roster, for the site-admin user list. <b>Instance admin only</b>,
    /// gated and audited exactly like <c>auditEvents</c> — the existing precedent for an
    /// admin-only read — including its deliberate departure from the schema's usual
    /// "absent, not forbidden" rule: a non-admin gets an explicit GraphQL error rather
    /// than an empty list. There is no existence to leak (everyone knows a user list
    /// exists), so an honest refusal is the more useful answer on an unambiguously
    /// admin-only screen, and §7 requires the denial be recorded either way.
    ///
    /// <para>Reading the roster is itself an audited read (<c>admin.users.view</c>), on
    /// the same reasoning that makes viewing the audit log audited: a list of every
    /// account, with email addresses and last-seen times, is exactly the kind of thing
    /// worth knowing someone pulled.</para>
    ///
    /// <para><b>Ordering is by display name, then id.</b> A roster is read by looking
    /// someone up, so alphabetical is what a person scanning it wants; id breaks ties so
    /// paging is stable across requests, which last-seen ordering would not be — it
    /// reorders under the reader as people sign in. Sorting by activity is a filter the
    /// UI can add later over a stable base.</para>
    ///
    /// <para><b>Paged with the relay connection, matching <c>auditEvents</c> rather than
    /// <c>search</c>.</b> This is the same shape of field as the audit viewer — an
    /// admin-only <c>IQueryable</c> with no per-row permission filter — so it gets the
    /// same treatment, and the admin section of the SPA then consumes one pattern rather
    /// than two. <c>IncludeTotalCount</c> is safe here for the reason it is safe there
    /// and is NOT safe for search (§9.1): nothing in this list is canView-filtered, so an
    /// exact count reveals nothing a row wouldn't. A roster in an organisation of any
    /// size is also precisely the list that should never have been unbounded.</para>
    /// </summary>
    [AuditAction("admin.users.view")]
    [UseAuditDispatch]
    // MaxPageSize 100, matching auditEvents — see its note. Same class of field
    // (admin-only, no per-row canView filter), same cap, and the roster page can step
    // its ?show= size to 100 without hitting a validation error the resolver never sees.
    [UsePaging(IncludeTotalCount = true, MaxPageSize = 100)]
    public async Task<IQueryable<AdminUser>> Users(
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            // Identical to auditEvents, including the silence for an anonymous caller:
            // there is no acting user to attribute a row to, and DbAuditSink refuses
            // UserId-less rows by design (§7), so recording unconditionally would make an
            // anonymous probe fail with that refusal instead of this field's own honest
            // one. The authenticated-but-not-admin case is the one that must stay loud.
            if (claimsPrincipal.Identity?.IsAuthenticated == true)
            {
                await auditSink.RecordAsync(
                    new AuditRecord(
                        "admin.users.view", AuditOutcome.Denied,
                        DetailsJson: JsonSerializer.Serialize(new { reason = "instance admin required" })),
                    cancellationToken);
            }

            throw new GraphQLException("Instance admin required to view the user list.");
        }

        // Projected in the database, not after materializing entities: AttributesJson is
        // never fetched at all, so it cannot reach a log, a heap dump, or a future field
        // selection by accident. HasAvatar is a correlated EXISTS in the same query — the
        // same one-round-trip shape UserRefByIdDataLoader uses, and provider-agnostic
        // LINQ so it translates on SQLite and SQL Server alike.
        // Ordered on the ENTITY, before the projection: ordering after it asks the
        // provider to sort by a property of a constructed AdminUser, which does not
        // translate and fails at execution rather than at build.
        return db.Users.AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Id)
            .Select(u => new AdminUser(
                u.Id,
                u.Subject,
                u.DisplayName,
                u.Email,
                u.IsExternal,
                db.UserAvatars.Any(a => a.UserId == u.Id),
                u.CreatedAtUtc,
                u.LastSeenAtUtc));
    }
}
