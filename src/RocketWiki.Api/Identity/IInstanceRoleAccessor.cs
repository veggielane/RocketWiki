using System.Security.Claims;

namespace RocketWiki.Api.Identity;

/// <summary>
/// design.md §6.5: whether the caller holds Keycloak's realm-level "admin" role —
/// "manage spaces, the attribute registry, and settings." Deliberately NOT an ABAC
/// concept: <c>Principal</c>/<c>EffectivePermissionCalculator</c> carry no notion of
/// "instance admin" at all (see that calculator's own doc — instance admins do not
/// bypass page restrictions, so there is nothing here for a role flag to bypass).
/// <c>ISpaceService</c>/<c>IAccessRuleService</c> take this as a plain caller-resolved
/// bool for the same reason <c>actingUserId</c> is caller-resolved rather than looked
/// up internally (design.md §11) — the service has no way to derive it itself.
///
/// Surfaced via a dedicated "roles" protocol mapper (rocketwiki-realm.json) rather
/// than relying on Keycloak's default nested <c>realm_access.roles</c> claim, matching
/// the same "flat, custom-named claim" pattern already used for groups/nationality —
/// ASP.NET Core's JWT bearer handler does not auto-flatten <c>realm_access</c> JSON
/// into individual role claims, so depending on the default shape would silently just
/// never work.
///
/// <para>The flat claim needs care of its own, though, and this is the one place in the
/// codebase that originally missed it: <c>roles</c> IS in
/// <c>JsonWebTokenHandler.DefaultInboundClaimTypeMap</c>, so with
/// <c>JwtBearerOptions.MapInboundClaims</c> at its default of <c>true</c> the handler
/// renames it to <see cref="ClaimTypes.Role"/> before anything here sees it. Reading
/// only <c>"roles"</c> therefore found nothing against a real token — every admin gate
/// refused every caller, and <c>createSpace</c> answered "instance admin required" to a
/// user holding the realm's <c>admin</c> role. That was invisible to the test tier
/// because <c>TestAuthHandler</c> builds its ClaimsPrincipal by hand and performs no
/// mapping, so a test setting <c>"roles"</c> sees <c>"roles"</c>. Both shapes are
/// accepted here for the same reason <c>Query.Me</c> reads <c>sub ?? NameIdentifier</c>
/// and <c>email ?? ClaimTypes.Email</c> — whether mapping is on is a host configuration
/// detail, not something an authorization decision should silently depend on.</para>
/// </summary>
public interface IInstanceRoleAccessor
{
    bool IsInstanceAdmin { get; }
}

public sealed class InstanceRoleAccessor(IHttpContextAccessor httpContextAccessor) : IInstanceRoleAccessor
{
    /// <summary>The realm role design.md §6.5 calls the instance admin.</summary>
    public const string AdminRole = "admin";

    /// <summary>The claim the Keycloak protocol mapper emits, before any inbound mapping.</summary>
    public const string RolesClaim = "roles";

    private bool? _cached;

    public bool IsInstanceAdmin
    {
        get
        {
            _cached ??= HasAdminRole(httpContextAccessor.HttpContext?.User);
            return _cached.Value;
        }
    }

    /// <summary>
    /// Both claim names are checked deliberately — see the interface's doc. Which one
    /// carries the role depends on <c>JwtBearerOptions.MapInboundClaims</c>, and an
    /// authorization decision must not turn on that.
    /// </summary>
    internal static bool HasAdminRole(ClaimsPrincipal? user) =>
        user is not null
        && (user.FindAll(RolesClaim).Any(c => c.Value == AdminRole)
            || user.FindAll(ClaimTypes.Role).Any(c => c.Value == AdminRole));
}
