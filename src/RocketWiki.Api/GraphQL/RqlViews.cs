using HotChocolate;
using RocketWiki.Core.Query;

namespace RocketWiki.Api.GraphQL;

/// <summary>Which of the four node shapes an <see cref="RqlNodeView"/> is.</summary>
[GraphQLName("RqlNodeKind")]
public enum RqlNodeKindView
{
    And,
    Or,
    Not,
    Predicate,
}

/// <summary>Which of the four value shapes an <see cref="RqlValueView"/> is.</summary>
[GraphQLName("RqlValueKind")]
public enum RqlValueKindView
{
    /// <summary>A label name, space key, title fragment, or user subject.</summary>
    Text,

    /// <summary>An ISO-8601 date or instant literal; <see cref="RqlValueView.Text"/> is its canonical spelling.</summary>
    Date,

    /// <summary><c>currentUser()</c>.</summary>
    CurrentUser,

    /// <summary><c>now()</c>, with <see cref="RqlValueView.NowOffset"/> set for <c>now("-7d")</c>.</summary>
    Now,
}

/// <summary>
/// One node of a parsed RQL filter tree, flattened into a single type with a discriminating
/// <c>kind</c> rather than modelled as a GraphQL union.
///
/// <para>A union would be tidier SDL and worse for the one consumer this exists for: a
/// structural query builder walks the tree generically, and a union forces every walk
/// through inline fragments for four cases that differ by three fields. The flattened shape
/// is also what keeps the AST strictly <b>output</b> — there is no corresponding input type
/// anywhere, deliberately (design.md §22: a client-built AST would need the same validation
/// applied to it, and two input paths is twice the surface to keep the closed field set
/// closed on).</para>
/// </summary>
[GraphQLName("RqlNode")]
public sealed record RqlNodeView(
    RqlNodeKindView Kind,
    IReadOnlyList<RqlNodeView> Children,
    RqlField? Field,
    RqlOperator? Operator,
    IReadOnlyList<RqlValueView> Values,
    int Offset,
    int Length);

/// <summary>One value on the right of a predicate.</summary>
[GraphQLName("RqlValue")]
public sealed record RqlValueView(RqlValueKindView Kind, string? Text, string? NowOffset);

/// <summary>One ORDER BY term, exactly as written — the execution default (<c>updated DESC</c>) is never materialized here.</summary>
[GraphQLName("RqlOrderItem")]
public sealed record RqlOrderItemView(RqlField Field, RqlSortDirection Direction);

/// <summary>
/// One problem with a query, positioned so an editor can underline it.
///
/// <para><b>Never says anything about existence.</b> design.md §6.7 requires an invisible
/// space to be indistinguishable from a nonexistent one, so these messages are about syntax
/// and vocabulary only: no message here can report that a space, label, or user is unknown,
/// restricted, or invisible, because validation never looks.</para>
/// </summary>
[GraphQLName("RqlError")]
public sealed record RqlErrorView(RqlErrorCode Code, string Message, int Offset, int Length)
{
    public static RqlErrorView From(RqlError error) =>
        new(error.Code, error.Message, error.Offset, error.Length);
}

/// <summary>The result of <c>parseRql</c>: the tree, the canonical string, or the errors.</summary>
[GraphQLName("RqlParseResult")]
public sealed record RqlParseResultView(
    bool IsValid,
    string? Canonical,
    RqlNodeView? Where,
    IReadOnlyList<RqlOrderItemView> OrderBy,
    IReadOnlyList<RqlErrorView> Errors)
{
    public static RqlParseResultView From(RqlParseResult result) =>
        result.Query is null
            ? new RqlParseResultView(false, null, null, [], result.Errors.Select(RqlErrorView.From).ToList())
            : new RqlParseResultView(
                true,
                result.Canonical,
                MapNode(result.Query.Where),
                result.Query.OrderBy.Select(i => new RqlOrderItemView(i.Field, i.Direction)).ToList(),
                []);

    private static RqlNodeView MapNode(RqlNode node) => node switch
    {
        RqlNode.And and => Composite(RqlNodeKindView.And, and.Children),
        RqlNode.Or or => Composite(RqlNodeKindView.Or, or.Children),
        RqlNode.Not not => Composite(RqlNodeKindView.Not, [not.Child]),
        RqlNode.Predicate predicate => new RqlNodeView(
            RqlNodeKindView.Predicate,
            [],
            predicate.Field,
            predicate.Operator,
            predicate.Values.Select(MapValue).ToList(),
            predicate.Span.Offset,
            predicate.Span.Length),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unhandled RQL node."),
    };

    /// <summary>
    /// A boolean node's span is derived from its children's, because the validated AST
    /// carries spans on predicates only — the level errors are reported at and the level an
    /// editor underlines. The derived extent covers the operands but not a leading
    /// <c>NOT</c> or the parentheses around a group; that is deliberate rather than missing,
    /// since a builder highlighting a subexpression wants the operands, and nothing reports
    /// an error at a composite node.
    /// </summary>
    private static RqlNodeView Composite(RqlNodeKindView kind, IReadOnlyList<RqlNode> children)
    {
        var mapped = children.Select(MapNode).ToList();
        var offset = mapped.Min(c => c.Offset);
        var end = mapped.Max(c => c.Offset + c.Length);
        return new RqlNodeView(kind, mapped, null, null, [], offset, end - offset);
    }

    private static RqlValueView MapValue(RqlValue value) => value switch
    {
        RqlValue.Text text => new RqlValueView(RqlValueKindView.Text, text.Value, null),
        RqlValue.Date date => new RqlValueView(RqlValueKindView.Date, date.CanonicalText, null),
        RqlValue.CurrentUser => new RqlValueView(RqlValueKindView.CurrentUser, null, null),
        RqlValue.Now now => new RqlValueView(RqlValueKindView.Now, null, now.Offset),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled RQL value."),
    };
}
