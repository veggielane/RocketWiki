using System.Security.Claims;
using RocketWiki.Core.Access;

namespace RocketWiki.Api.Identity;

/// <summary>
/// The actual claim-to-Principal parsing logic (design.md §6.1), extracted out of
/// <see cref="CurrentPrincipalAccessor"/> so it can also be called somewhere that
/// cannot use <see cref="IHttpContextAccessor"/> at all: a SignalR Hub method
/// invocation over an already-established connection is not a fresh request going
/// through the ASP.NET Core middleware pipeline the way a GraphQL/REST call is, so
/// <c>IHttpContextAccessor.HttpContext</c> is not reliably populated inside one —
/// the same class of cross-DI-scope trap this project already hit once with Hot
/// Chocolate resolvers (see <see cref="RocketWiki.Api.Audit.ICurrentAuditContextAccessor"/>'s
/// doc), just surfacing in a new place. A Hub's own <c>HubCallerContext.User</c> is
/// SignalR's reliable equivalent there, so <see cref="RealTime.NotificationsHub"/>
/// calls this directly instead of going through <see cref="ICurrentPrincipalAccessor"/>.
/// </summary>
public static class PrincipalBuilder
{
    public static Principal? Build(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var groups = user.FindAll("groups").Select(c => c.Value);

        // Registered attributes (design.md §6.2) - nationality is the only one wired end
        // to end today. Absent entirely (not an empty list) when the claim isn't present,
        // matching Principal's own fail-closed contract for a key nobody holds a value for.
        var nationality = user.FindAll("nationality").Select(c => c.Value).ToArray();
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? attributes = nationality.Length > 0
            ? [new KeyValuePair<string, IReadOnlyList<string>>("nationality", nationality)]
            : null;

        return Principal.Create(subject, groups, attributes);
    }
}
