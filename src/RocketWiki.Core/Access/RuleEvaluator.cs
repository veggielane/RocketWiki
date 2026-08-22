namespace RocketWiki.Core.Access;

/// <summary>
/// Pure, in-process evaluation of an already-parsed rule tree against a principal.
/// design.md §6.3/§6.7: no NOT, no deny rules, no admin bypass — this type has no
/// concept of "admin" at all, so none can slip in. Empty allOf/anyOf never evaluate to
/// the mathematically vacuous `true`; they fail closed to `false` instead, since
/// <see cref="RuleExpressionSerializer"/> already rejects empty combinators at parse
/// time and a tree built any other way (e.g. programmatically) must not accidentally
/// grant access.
/// </summary>
public static class RuleEvaluator
{
    public static bool Evaluate(RuleNode node, Principal principal) => node switch
    {
        EveryoneCondition => true,
        GroupCondition g => principal.Groups.Contains(g.Group),
        UserCondition u => string.Equals(principal.UserId, u.UserId, StringComparison.Ordinal),
        AttrCondition a => principal.Attributes.TryGetValue(a.Attribute, out var values)
            && values.Any(v => a.In.Contains(v, StringComparer.Ordinal)),
        AllOfNode { Children.Count: 0 } => false,
        AllOfNode allOf => allOf.Children.All(c => Evaluate(c, principal)),
        AnyOfNode { Children.Count: 0 } => false,
        AnyOfNode anyOf => anyOf.Children.Any(c => Evaluate(c, principal)),
        _ => false,
    };
}
