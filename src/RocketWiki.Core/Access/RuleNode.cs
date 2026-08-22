namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.3: a rule expression tree. No NOT and no deny rules — allow-list
/// thinking only. Every concrete node below corresponds exactly to one recognized
/// JSON shape; see <see cref="RuleExpressionSerializer"/> for the mapping.
/// </summary>
public abstract record RuleNode;

/// <summary>AND: every child must match.</summary>
public sealed record AllOfNode(IReadOnlyList<RuleNode> Children) : RuleNode;

/// <summary>OR: at least one child must match.</summary>
public sealed record AnyOfNode(IReadOnlyList<RuleNode> Children) : RuleNode;

/// <summary>Matches if the principal is a member of <paramref name="Group"/>.</summary>
public sealed record GroupCondition(string Group) : RuleNode;

/// <summary>Matches if the principal's user id equals <paramref name="UserId"/>.</summary>
public sealed record UserCondition(string UserId) : RuleNode;

/// <summary>
/// Matches if the principal holds <paramref name="Attribute"/> and at least one of its
/// values is in <paramref name="In"/>. A principal missing the attribute never matches
/// (fail closed) — see design.md §6.3.
/// </summary>
public sealed record AttrCondition(string Attribute, IReadOnlyList<string> In) : RuleNode;

/// <summary>Matches any authenticated principal.</summary>
public sealed record EveryoneCondition : RuleNode;
