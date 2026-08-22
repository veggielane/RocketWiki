namespace RocketWiki.Core.Access;

/// <summary>
/// Entry point that ties parsing and evaluation together with fail-closed semantics:
/// a rule that fails to parse denies access exactly like a rule that parses but
/// doesn't match (design.md §6.3).
/// </summary>
public static class AccessRuleExpression
{
    public static RuleEvaluationResult Evaluate(string expressionJson, Principal principal)
    {
        if (!RuleExpressionSerializer.TryParse(expressionJson, out var node, out var error))
        {
            return RuleEvaluationResult.Malformed(error!);
        }

        return RuleEvaluator.Evaluate(node!, principal)
            ? RuleEvaluationResult.Matched()
            : RuleEvaluationResult.NotMatched();
    }
}
