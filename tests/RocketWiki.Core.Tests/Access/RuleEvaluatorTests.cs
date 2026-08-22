using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

public class RuleEvaluatorTests
{
    private static Principal MakePrincipal(
        string userId = "user-1",
        string[]? groups = null,
        Dictionary<string, string[]>? attributes = null)
    {
        var attrs = attributes?.Select(kv =>
            new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, kv.Value));
        return Principal.Create(userId, groups ?? Array.Empty<string>(), attrs);
    }

    [Fact]
    public void Everyone_AlwaysMatches()
    {
        var principal = MakePrincipal();

        Assert.True(RuleEvaluator.Evaluate(new EveryoneCondition(), principal));
    }

    [Fact]
    public void Group_MatchesWhenPrincipalIsMember()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });

        Assert.True(RuleEvaluator.Evaluate(new GroupCondition("engineering"), principal));
    }

    [Fact]
    public void Group_UnknownGroupOnPrincipal_FailsClosed()
    {
        // design.md §12: a group unknown on this instance (e.g. after sync from a
        // different Keycloak realm) must match nobody, not throw and not default-allow.
        var principal = MakePrincipal(groups: new[] { "marketing" });

        Assert.False(RuleEvaluator.Evaluate(new GroupCondition("engineering"), principal));
    }

    [Fact]
    public void User_MatchesExactUserId()
    {
        var principal = MakePrincipal(userId: "sub-123");

        Assert.True(RuleEvaluator.Evaluate(new UserCondition("sub-123"), principal));
        Assert.False(RuleEvaluator.Evaluate(new UserCondition("sub-999"), principal));
    }

    [Fact]
    public void Attr_MatchesWhenAnyHeldValueIsInList()
    {
        var principal = MakePrincipal(attributes: new()
        {
            ["nationality"] = new[] { "NZ" },
        });

        Assert.True(RuleEvaluator.Evaluate(new AttrCondition("nationality", new[] { "NZ", "US" }), principal));
    }

    [Fact]
    public void Attr_DualNational_MatchesOnAnyHeldValue()
    {
        var principal = MakePrincipal(attributes: new()
        {
            ["nationality"] = new[] { "GB", "US" },
        });

        Assert.True(RuleEvaluator.Evaluate(new AttrCondition("nationality", new[] { "NZ", "US" }), principal));
    }

    [Fact]
    public void Attr_NoOverlap_DoesNotMatch()
    {
        var principal = MakePrincipal(attributes: new()
        {
            ["nationality"] = new[] { "FR" },
        });

        Assert.False(RuleEvaluator.Evaluate(new AttrCondition("nationality", new[] { "NZ", "US" }), principal));
    }

    [Fact]
    public void Attr_MissingAttribute_FailsClosed()
    {
        // design.md §6.3: "a user with no nationality attribute matches no attr condition".
        var principal = MakePrincipal(attributes: new());

        Assert.False(RuleEvaluator.Evaluate(new AttrCondition("nationality", new[] { "NZ", "US" }), principal));
    }

    [Fact]
    public void AllOf_AllChildrenMatch_Matches()
    {
        var principal = MakePrincipal(groups: new[] { "engineering", "export-cleared" });
        var node = new AllOfNode(new RuleNode[]
        {
            new GroupCondition("engineering"),
            new GroupCondition("export-cleared"),
        });

        Assert.True(RuleEvaluator.Evaluate(node, principal));
    }

    [Fact]
    public void AllOf_OneChildFails_DoesNotMatch()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var node = new AllOfNode(new RuleNode[]
        {
            new GroupCondition("engineering"),
            new GroupCondition("export-cleared"),
        });

        Assert.False(RuleEvaluator.Evaluate(node, principal));
    }

    [Fact]
    public void AllOf_Empty_FailsClosed_NotVacuouslyTrue()
    {
        // The parser never produces this (empty allOf is rejected at parse time), but
        // the evaluator must not treat an empty AND as mathematically-vacuous `true`
        // if a tree is ever constructed some other way - that would silently grant access.
        var principal = MakePrincipal();
        var node = new AllOfNode(Array.Empty<RuleNode>());

        Assert.False(RuleEvaluator.Evaluate(node, principal));
    }

    [Fact]
    public void AnyOf_OneChildMatches_Matches()
    {
        var principal = MakePrincipal(groups: new[] { "export-cleared" });
        var node = new AnyOfNode(new RuleNode[]
        {
            new AttrCondition("nationality", new[] { "NZ", "US" }),
            new GroupCondition("export-cleared"),
        });

        Assert.True(RuleEvaluator.Evaluate(node, principal));
    }

    [Fact]
    public void AnyOf_NoChildMatches_DoesNotMatch()
    {
        var principal = MakePrincipal();
        var node = new AnyOfNode(new RuleNode[]
        {
            new AttrCondition("nationality", new[] { "NZ", "US" }),
            new GroupCondition("export-cleared"),
        });

        Assert.False(RuleEvaluator.Evaluate(node, principal));
    }

    [Fact]
    public void AnyOf_Empty_FailsClosed_NotVacuouslyFalseButSafe()
    {
        var principal = MakePrincipal();
        var node = new AnyOfNode(Array.Empty<RuleNode>());

        Assert.False(RuleEvaluator.Evaluate(node, principal));
    }

    [Fact]
    public void NestedExpression_FromDesignDoc_MatchesViaNationality()
    {
        // { allOf: [ {group: engineering}, {anyOf: [ {attr: nationality in [NZ,US]}, {group: export-cleared} ]} ] }
        var node = RuleExpressionSerializer.Parse("""
        {
          "allOf": [
            { "group": "engineering" },
            { "anyOf": [
              { "attr": "nationality", "in": ["NZ", "US"] },
              { "group": "export-cleared" }
            ]}
          ]
        }
        """);

        var principalWithNationality = MakePrincipal(
            groups: new[] { "engineering" },
            attributes: new() { ["nationality"] = new[] { "US" } });
        var principalWithClearance = MakePrincipal(groups: new[] { "engineering", "export-cleared" });
        var principalNeither = MakePrincipal(groups: new[] { "engineering" });
        var principalNotEngineering = MakePrincipal(
            groups: new[] { "export-cleared" },
            attributes: new() { ["nationality"] = new[] { "US" } });

        Assert.True(RuleEvaluator.Evaluate(node, principalWithNationality));
        Assert.True(RuleEvaluator.Evaluate(node, principalWithClearance));
        Assert.False(RuleEvaluator.Evaluate(node, principalNeither));
        Assert.False(RuleEvaluator.Evaluate(node, principalNotEngineering));
    }
}
