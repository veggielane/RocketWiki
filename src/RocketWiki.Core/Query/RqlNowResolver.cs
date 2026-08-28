namespace RocketWiki.Core.Query;

/// <summary>
/// Replaces every <see cref="RqlValue.Now"/> in a query with a concrete instant, computed
/// from <b>one</b> evaluation time (design.md §22).
///
/// <para>That "one" is the whole point, and it is why this is a separate pass rather than a
/// <c>DateTime.UtcNow</c> read inside the compiler. A query like
/// <c>updated &gt;= now("-1d") AND updated &lt; now()</c> evaluated against a clock read per
/// predicate can produce a window that is not the window the author asked for — and across
/// a midnight or a leap second, one that is empty. Resolving once also makes the behaviour
/// testable without a clock: the caller passes the instant.</para>
///
/// <para>The result is for the compiler only. It is never printed: printing a resolved
/// query would replace <c>now("-7d")</c> with the timestamp it happened to mean at one
/// moment, and storing that back would freeze a rolling filter.</para>
/// </summary>
public static class RqlNowResolver
{
    public static RqlQuery Resolve(RqlQuery query, DateTime evaluatedAtUtc) =>
        new(ResolveNode(query.Where, evaluatedAtUtc), query.OrderBy);

    private static RqlNode ResolveNode(RqlNode node, DateTime evaluatedAtUtc) => node switch
    {
        RqlNode.And and => new RqlNode.And(and.Children.Select(c => ResolveNode(c, evaluatedAtUtc)).ToList()),
        RqlNode.Or or => new RqlNode.Or(or.Children.Select(c => ResolveNode(c, evaluatedAtUtc)).ToList()),
        RqlNode.Not not => new RqlNode.Not(ResolveNode(not.Child, evaluatedAtUtc)),
        RqlNode.Predicate predicate => new RqlNode.Predicate(
            predicate.Field,
            predicate.Operator,
            predicate.Values.Select(v => ResolveValue(v, evaluatedAtUtc)).ToList(),
            predicate.Span),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unhandled RQL node."),
    };

    private static RqlValue ResolveValue(RqlValue value, DateTime evaluatedAtUtc)
    {
        if (value is not RqlValue.Now now)
        {
            return value;
        }

        var instant = evaluatedAtUtc;
        if (now.Offset is not null && RqlDateLiteral.TryParseOffset(now.Offset, out var offset, out _))
        {
            instant = evaluatedAtUtc + offset;
        }

        // A resolved now() is an instant, never a day range: `updated > now("-7d")` means
        // "in the last seven days to the minute", not "since the start of that day".
        return new RqlValue.Date(instant, null, RqlDateLiteral.FormatInstant(instant)) { Span = value.Span };
    }
}
