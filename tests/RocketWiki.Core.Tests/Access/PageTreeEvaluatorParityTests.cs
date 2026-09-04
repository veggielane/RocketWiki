using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.9: the tree walk decides each node with
/// <see cref="EffectivePermissionCalculator.EvaluateViewGates"/> — the view half of the
/// ladder after S, over the node's OWN restrictions — and <c>GetPageAsync</c> decides the
/// same page with <see cref="EffectivePermissionCalculator.Compute"/> over the whole
/// chain. For a page whose chain is its own rules (a root, or a node whose ancestors all
/// passed) the two must agree verdict for verdict and reason for reason, or the tree
/// would show a page the id fetch refuses, or hide one it allows. Driven over a few
/// hundred random (marking, own rules, principal, granted union) quadruples from a fixed
/// seed, so the property is checked across the whole gate space rather than at a
/// hand-picked corner.
/// </summary>
public class PageTreeEvaluatorParityTests
{
    private static readonly Guid PageId = new("aaaaaaaa-0000-0000-0000-0000000000ee");
    private static readonly SelectorValue Codeword = new("CODEWORD", "ZEBRA");

    [Fact]
    public void EvaluateViewGates_AgreesWithCompute_OverRandomMarkingsRulesPrincipalsAndGrants()
    {
        var random = new Random(20260903);
        var levels = Enum.GetValues<ClassificationLevel>();
        var countries = new[] { "UK", "US", "NZ" };
        var selectors = new[] { TestCatalogs.Apple, TestCatalogs.North, Codeword };
        var grantable = new[] { TestCatalogs.Apple, TestCatalogs.North, TestCatalogs.Banana, Codeword };
        var groups = new[] { "engineering", "legal", "nobody-is-in-this" };

        var denials = 0;
        for (var i = 0; i < 500; i++)
        {
            // Probabilities are skewed towards passing so that both outcomes are common:
            // with four independent gates an even coin per gate denies almost every row.
            var marking = ProtectiveMarking.Create(
                levels[random.Next(levels.Length)],
                countries.Where(_ => random.Next(5) == 0).ToArray(),
                selectors.Where(sel => random.Next(sel == Codeword ? 8 : 4) == 0).ToArray());

            var ownRules = Enumerable.Range(0, random.Next(3))
                .Select(_ => Restriction(random.Next(2) == 0 ? PageAction.View : PageAction.Edit, groups[random.Next(groups.Length)]))
                .ToList();

            var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
            if (random.Next(4) != 0)
            {
                attributes.Add(new("nationality", [countries[random.Next(countries.Length)]]));
            }

            var principal = Principal.Create(
                "user-sub", groups.Take(2).Where(_ => random.Next(3) != 0).ToArray(), attributes);

            var granted = grantable.Where(_ => random.Next(4) != 0).ToArray();
            var access = new SpaceAccess(new HashSet<SelectorValue>(granted));

            var stopped = EffectivePermissionCalculator.EvaluateViewGates(
                access, marking, ownRules, TestCatalogs.Fruit, principal, shortCircuit: true);
            var full = EffectivePermissionCalculator.EvaluateViewGates(
                access, marking, ownRules, TestCatalogs.Fruit, principal, shortCircuit: false);
            var verdict = EffectivePermissionCalculator.Compute(
                new PermissionInputs([AccessGrant(granted)], ownRules, false, marking, TestCatalogs.Fruit), principal);

            var firstFailed = stopped.FirstOrDefault(g => !g.Passed);
            Assert.Equal(firstFailed is null, verdict.CanView);
            Assert.Equal(firstFailed?.Reason, verdict.ViewDenialReason);
            // The full form names the same first failure and is otherwise a superset.
            Assert.Equal(firstFailed?.Reason, full.FirstOrDefault(g => !g.Passed)?.Reason);
            Assert.True(full.Count >= stopped.Count);
            if (firstFailed is not null)
            {
                denials++;
            }
        }

        // Non-vacuity: the random walk must have exercised both outcomes.
        Assert.InRange(denials, 50, 450);
    }

    private static AccessRule AccessGrant(IEnumerable<SelectorValue> granted)
    {
        var rule = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = Guid.NewGuid(),
            ExpressionJson = """{ "everyone": true }""",
        };
        foreach (var selector in granted)
        {
            rule.Selectors.Add(new AccessRuleSelector { AccessRuleId = rule.Id, Category = selector.Category, Value = selector.Value });
        }

        return rule;
    }

    private static AccessRule Restriction(PageAction action, string group) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = PageId,
        Action = action,
        ExpressionJson = $$"""{ "group": "{{group}}" }""",
    };
}
