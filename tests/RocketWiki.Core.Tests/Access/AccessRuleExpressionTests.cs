using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

public class AccessRuleExpressionTests
{
    private static Principal MakePrincipal(string[]? groups = null) =>
        Principal.Create("user-1", groups ?? Array.Empty<string>());

    [Fact]
    public void Evaluate_MalformedJson_DeniesAndFlagsMalformed()
    {
        var principal = MakePrincipal();

        var result = AccessRuleExpression.Evaluate("""{ "bogus": true }""", principal);

        Assert.False(result.IsMatch);
        Assert.True(result.IsMalformed);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Evaluate_ValidButNonMatchingRule_DeniesWithoutMalformedFlag()
    {
        var principal = MakePrincipal();

        var result = AccessRuleExpression.Evaluate("""{ "group": "engineering" }""", principal);

        Assert.False(result.IsMatch);
        Assert.False(result.IsMalformed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_ValidMatchingRule_Matches()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });

        var result = AccessRuleExpression.Evaluate("""{ "group": "engineering" }""", principal);

        Assert.True(result.IsMatch);
        Assert.False(result.IsMalformed);
    }

    [Fact]
    public void Evaluate_MalformedAndNonMatching_BothDeny_CallerCannotTellApartWithoutInspectingFlag()
    {
        var principal = MakePrincipal();

        var malformed = AccessRuleExpression.Evaluate("""{ "bogus": true }""", principal);
        var nonMatching = AccessRuleExpression.Evaluate("""{ "group": "engineering" }""", principal);

        Assert.Equal(malformed.IsMatch, nonMatching.IsMatch);
        Assert.False(malformed.IsMatch);
        Assert.False(nonMatching.IsMatch);
    }
}
