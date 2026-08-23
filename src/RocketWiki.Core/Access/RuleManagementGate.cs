using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.5.2's rule-management gate, stated exactly once: managing a space's
/// access rules — and, since a management UI is useless blind, *reading* them in
/// management detail — requires instance <c>admin</c> OR that space's own
/// <c>space-admin</c>. <see cref="Data" />-side <c>AccessRuleService</c> (the mutation
/// gate) and the API's restriction/grant read resolvers both call this one function,
/// so the write gate and the read gate can never drift apart — forking this check was
/// the specific anti-pattern to avoid.
///
/// Deliberately NOT part of <see cref="EffectivePermissionCalculator"/>: that class
/// carries no notion of "instance admin" at all, by design (§6.5 — admins do not
/// bypass page restrictions, so the calculator has nothing an admin flag could
/// bypass). Rule *management* is the one place §6.5.2 grants the admin role an arm,
/// and this type exists to keep that arm out of the enforcement calculator.
/// <paramref name="isInstanceAdmin"/> is a caller-resolved bool from the token's
/// realm roles (IInstanceRoleAccessor), same pattern as IAccessRuleService.
/// </summary>
public static class RuleManagementGate
{
    public static bool CanManageRules(SpaceRole? spaceRole, bool isInstanceAdmin) =>
        isInstanceAdmin || spaceRole == SpaceRole.SpaceAdmin;
}
