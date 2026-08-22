namespace RocketWiki.Core.Access;

/// <summary>
/// Thrown when an AccessRule's ExpressionJson does not parse into a recognized,
/// fully-valid rule tree. design.md §6.3: "a malformed or unevaluable rule denies
/// access and logs an error" — callers should treat this as a deny and log it; see
/// <see cref="AccessRuleExpression.Evaluate"/> for the non-throwing entry point used
/// during normal request evaluation.
/// </summary>
public sealed class MalformedRuleExpressionException : Exception
{
    public MalformedRuleExpressionException(string message) : base(message)
    {
    }
}
