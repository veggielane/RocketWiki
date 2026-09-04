using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// The people-picker directory: every user, by display name, readable by any
    /// authenticated caller. Backs the space-owner picker and, later, the rule builder.
    ///
    /// <para><b>Returns <see cref="UserRef"/> — the existing display-safe projection —
    /// and that choice is the security design, not a convenience.</b> <c>UserRef</c> is
    /// exactly <c>id</c>, <c>displayName</c>, <c>hasAvatar</c>: the same three facts the
    /// schema already shows every authenticated caller on every comment byline and
    /// attachment uploader. So this field widens no exposure surface — it makes an
    /// already-visible projection enumerable, which is a real difference but a much
    /// smaller one than a new shape would be. Reusing the type also means the "no leak"
    /// property is <b>structural</b>: there is no field here to forget to remove.</para>
    ///
    /// <para><b>What is deliberately absent, and why each one matters:</b></para>
    /// <list type="bullet">
    /// <item><description><b>Email and last-seen.</b> A directory that showed every
    /// address and everyone's activity times is a surveillance surface (§15's spirit:
    /// operational data is not a product feature). They stay on <c>AdminUser</c>, behind
    /// the instance-admin gate, where reading the roster is itself audited.</description></item>
    /// <item><description><b>Groups, nationality — any registered attribute.</b>
    /// These are the rule engine's INPUTS (§6.2, §6.1). A directory carrying them would
    /// be a who-holds-what census: an attacker with one ordinary account could
    /// enumerate the whole organisation's caveats and work out exactly whose credentials
    /// are worth stealing to reach a given compartment. That is a worse disclosure than
    /// most page content, and it is why this projection is narrow rather than "the User
    /// entity minus a couple of columns".</description></item>
    /// </list>
    ///
    /// <para>The admin <c>users</c> query is untouched and stays instance-admin-only with
    /// its richer fields and its <c>admin.users.view</c> audit row. This is a second,
    /// minimal field beside it — deliberately not a relaxation of that one, because
    /// loosening a gate is how the rich fields would have escaped.</para>
    ///
    /// <para><b>Projected in the database</b>, exactly like <c>Users</c>:
    /// <c>AttributesJson</c> is never fetched at all, so it cannot reach a log, a heap
    /// dump, or a future field selection by accident.</para>
    /// </summary>
    [NoAudit("Enumerates the display-safe UserRef projection (id, display name, avatar flag) that every authenticated caller already sees on comment bylines and attachment uploaders. No wiki content, no principal attributes, and no per-subject access decision - the same reasoning that leaves display-name resolution and the emoji vocabulary unaudited (design.md §7). The rich, admin-only roster remains `users`, which IS audited.")]
    // MaxPageSize 100, matching `users` and `auditEvents`. Safe against the field-cost
    // budget at that size for a reason worth stating: every UserRef field is an unweighted
    // scalar, so a row costs nothing and `first: 100` stays far under the 1000 budget —
    // unlike the home feeds, whose rows resolve objects and cap at 20. Nothing here needs
    // re-measuring unless UserRef gains a resolver-backed field.
    [UsePaging(IncludeTotalCount = true, MaxPageSize = 100)]
    public IQueryable<UserRef> UserDirectory(
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        string? search = null)
    {
        if (claimsPrincipal.Identity?.IsAuthenticated != true)
        {
            // Empty shape rather than an error, the convention every read root uses for
            // an anonymous caller (§6.7). Nothing here is secret from a signed-in user,
            // so there is no existence to protect - this is just the honest answer to
            // "who is in this directory" when nobody is asking.
            return Enumerable.Empty<UserRef>().AsQueryable();
        }

        var users = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Contains, not StartsWith: a picker is used to find "Priya" by typing part
            // of a surname as often as a first name. Provider-agnostic so it translates
            // on SQLite and SQL Server alike (§14) - no EF.Functions.Like, which would
            // put provider-specific syntax in a shared query.
            var term = search.Trim();
            users = users.Where(u => u.DisplayName.Contains(term));
        }

        // Ordered on the ENTITY before the projection - ordering after it asks the
        // provider to sort by a property of a constructed UserRef, which does not
        // translate and fails at execution rather than at build (the exact mistake the
        // admin roster query hit). Id breaks ties so paging is stable.
        return users
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Id)
            .Select(u => new UserRef(
                u.Id,
                u.DisplayName,
                db.UserAvatars.Any(a => a.UserId == u.Id)));
    }
}
