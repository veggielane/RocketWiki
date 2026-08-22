namespace RocketWiki.Core.Enums;

/// <summary>
/// data-model.md: AccessRule.Kind (tinyint). One table, two kinds, distinguished by
/// which nullable FK/role/action columns are populated (enforced by a check constraint
/// in RocketWiki.Data).
/// </summary>
public enum AccessRuleKind : byte
{
    SpaceGrant = 1,
    PageRestriction = 2,
}
