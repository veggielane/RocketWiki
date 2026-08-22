namespace RocketWiki.Core.Access;

/// <summary>
/// Result of evaluating one AccessRule's ExpressionJson. `IsMatch` is `false` for both
/// a legitimate non-match and a malformed rule — callers must not distinguish them for
/// access decisions (both deny), but `IsMalformed` lets a caller log/audit the
/// distinction (design.md §6.3: a malformed rule "denies access and logs an error").
/// </summary>
public readonly record struct RuleEvaluationResult(bool IsMatch, bool IsMalformed, string? Error)
{
    public static RuleEvaluationResult Matched() => new(true, false, null);

    public static RuleEvaluationResult NotMatched() => new(false, false, null);

    public static RuleEvaluationResult Malformed(string error) => new(false, true, error);
}
