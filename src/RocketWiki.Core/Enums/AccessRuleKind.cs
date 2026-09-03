namespace RocketWiki.Core.Enums;

/// <summary>
/// data-model.md: AccessRule.Kind (tinyint). One table, three kinds, distinguished by
/// which nullable FK/role/action columns are populated (enforced by
/// <c>CK_AccessRules_KindColumnPairing</c> in RocketWiki.Data).
///
/// <para>design.md §6.4 separates <b>seeing</b> from <b>doing</b>: an
/// <see cref="AccessGrant"/> says who may see a space's data (and which selector values
/// they are granted there, §21.15); a <see cref="RoleGrant"/> — the ROLE grant — says
/// who may edit or administer it, and confers no visibility at all. Roles never
/// supersede access. The numeric values are storage and audit-JSON facts: kind 1 was the
/// only grant kind before the split, and every existing row and every
/// <c>permission.change</c> audit row still says 1.</para>
/// </summary>
public enum AccessRuleKind : byte
{
    /// <summary>A role grant: <c>SpaceId</c> + <c>Role</c> (Editor or SpaceAdmin). Confers
    /// no visibility — a holder with no matching <see cref="AccessGrant"/> manages a space
    /// whose pages they cannot read (§6.5.2).</summary>
    RoleGrant = 1,

    /// <summary>A page restriction: <c>PageId</c> + <c>Action</c>. Only ever subtracts (§6.4).</summary>
    PageRestriction = 2,

    /// <summary>An access grant: <c>SpaceId</c>, no role, no action; may carry selector
    /// rows. Matching one is the S gate — the precondition for seeing anything in the
    /// space — and the union of the matched grants' selectors is what G checks a page's
    /// selectors against (§21.15).</summary>
    AccessGrant = 3,
}
