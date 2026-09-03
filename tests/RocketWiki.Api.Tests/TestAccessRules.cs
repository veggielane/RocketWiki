using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Tests;

/// <summary>
/// Grant fixtures for the integration tests, stated once. design.md §6.4: a role grant
/// confers no visibility, so a fixture that means "an editor (or admin) of this space"
/// needs an access grant beside the role grant — the same mirror row the split migration
/// writes for a pre-split Editor or Space-admin grant. Fixtures that mean "a role without
/// access" add the role grant directly and deliberately.
/// </summary>
internal static class TestAccessRules
{
    /// <summary>The rule itself, preceded by its mirror access grant when it is a role
    /// grant; any other kind passes through alone.</summary>
    public static IEnumerable<AccessRule> WithAccessBesideRole(AccessRule rule)
    {
        if (rule.Kind == AccessRuleKind.RoleGrant)
        {
            yield return new AccessRule
            {
                Kind = AccessRuleKind.AccessGrant,
                SpaceId = rule.SpaceId,
                ExpressionJson = rule.ExpressionJson,
                CreatedAtUtc = rule.CreatedAtUtc,
                CreatedByUserId = rule.CreatedByUserId,
                UpdatedAtUtc = rule.UpdatedAtUtc,
                UpdatedByUserId = rule.UpdatedByUserId,
            };
        }

        yield return rule;
    }
}
