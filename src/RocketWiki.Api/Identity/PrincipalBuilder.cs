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

        // Registered attributes (design.md §6.2). Each is absent entirely (not an empty
        // list) when its claim isn't present, matching Principal's own fail-closed
        // contract for a key nobody holds a value for - which is what makes
        // ClearanceGate.ResolveClearance's "absent means OFFICIAL" and AttrCondition's
        // "absent matches nothing" both land on the intended answer rather than on an
        // empty-string comparison.
        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        AddIfPresent(attributes, user, ClearanceGate.NationalityAttributeKey);

        // design.md §21: the clearance attribute gates every page read against its
        // protective marking. It is an ordinary registered attribute - no special
        // plumbing, no separate accessor - precisely so it inherits §6.1's "evaluate the
        // token, never the local User mirror" for free.
        AddIfPresent(attributes, user, ClearanceGate.ClearanceAttributeKey);

        return Principal.Create(subject, groups, attributes.Count > 0 ? attributes : null);
    }

    private static void AddIfPresent(
        List<KeyValuePair<string, IReadOnlyList<string>>> attributes, ClaimsPrincipal user, string claimName)
    {
        var values = user.FindAll(claimName).Select(c => c.Value).ToArray();
        if (values.Length > 0)
        {
            attributes.Add(new KeyValuePair<string, IReadOnlyList<string>>(claimName, values));
        }
    }
}
