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
///
/// <para><b>An instance, not a static, since selectors exist</b> (design.md §21.15):
/// which claims are mapped is no longer a compile-time list. Beside <c>groups</c>,
/// <c>nationality</c> and <c>clearance</c>, every configured selector category's
/// <see cref="SelectorCategory.ClaimName"/> is mapped from the token — and ONLY those,
/// so a claim nobody configured never becomes a principal attribute a rule could match
/// on by accident. The catalog is the single source of that list; it is the same
/// singleton the gates read, so the builder and the gate cannot disagree about which
/// claim gates which category. Values travel as-is: <see cref="SelectorGate"/> decides
/// that only <c>yes</c> counts, and it decides it once.</para>
///
/// <para>Registered as a singleton (the catalog is immutable) and injected into both
/// callers, so the HTTP path and the hub path build the identical Principal from the
/// identical claim list — co-edit join and eviction honour selectors with no second
/// implementation.</para>
/// </summary>
public sealed class PrincipalBuilder(SelectorCatalog catalog)
{
    public Principal? Build(ClaimsPrincipal? user)
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
        // ClearanceGate.ResolveClearance's "absent means the floor" and AttrCondition's
        // "absent matches nothing" both land on the intended answer rather than on an
        // empty-string comparison.
        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        AddIfPresent(attributes, user, ClearanceGate.NationalityAttributeKey);

        // design.md §21: the clearance attribute gates every page read against its
        // protective marking. It is an ordinary registered attribute - no special
        // plumbing, no separate accessor - precisely so it inherits §6.1's "evaluate the
        // token, never the local User mirror" for free.
        AddIfPresent(attributes, user, ClearanceGate.ClearanceAttributeKey);

        // design.md §21.15: one attribute per selector claim the instance configured.
        // The catalog already refused a claim name that collides with the three above
        // (SelectorCatalog.ReservedClaimNames), so nothing here can be mapped twice.
        foreach (var claimName in catalog.ClaimNames)
        {
            AddIfPresent(attributes, user, claimName);
        }

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
