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
/// </summary>
public interface IInstanceRoleAccessor
{
    bool IsInstanceAdmin { get; }
}

public sealed class InstanceRoleAccessor(IHttpContextAccessor httpContextAccessor) : IInstanceRoleAccessor
{
    private bool? _cached;

    public bool IsInstanceAdmin
    {
        get
        {
            _cached ??= httpContextAccessor.HttpContext?.User.FindAll("roles").Any(c => c.Value == "admin") ?? false;
            return _cached.Value;
        }
    }
}
