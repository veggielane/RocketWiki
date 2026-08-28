namespace RocketWiki.Core.Query;

/// <summary>
/// One parsed, validated RQL query (design.md §22). <see cref="Where"/> is the boolean
/// filter; <see cref="OrderBy"/> is exactly what the author wrote — <b>empty when they
/// wrote no ORDER BY at all</b>, never back-filled with the execution default
/// (<c>updated DESC</c>).
///
/// <para>That emptiness is load-bearing rather than lazy. The builder UI round-trips
/// structurally: build → <see cref="RqlPrinter">print</see> → store → reparse. If parsing
/// materialized the default, the printer would emit it, and a query that merely passed
/// through the builder would silently grow an <c>ORDER BY updated DESC</c> clause it never
/// had — the round trip would stop being a fixed point. The default belongs to execution,
/// which is where it is applied.</para>
/// </summary>
public sealed record RqlQuery(RqlNode Where, IReadOnlyList<RqlOrderItem> OrderBy);

/// <summary>One ORDER BY term. Direction is always explicit here; a bare <c>title</c> parses as <c>title ASC</c>.</summary>
public sealed record RqlOrderItem(RqlField Field, RqlSortDirection Direction, RqlSpan Span);

/// <summary>
/// A node of the boolean filter tree. Closed hierarchy (private constructor, nested
/// derivations) so a consumer's <c>switch</c> over the four shapes is exhaustive and a
/// fifth cannot appear from outside this file.
/// </summary>
public abstract record RqlNode
{
    private RqlNode()
    {
    }

    /// <summary>
    /// Conjunction of two or more children. N-ary rather than binary on purpose: the
    /// parser flattens <c>a AND b AND c</c> and the redundantly parenthesized
    /// <c>(a AND b) AND c</c> to the same node, which is what makes
    /// <c>Print(Parse(x))</c> a fixed point.
    /// </summary>
    public sealed record And(IReadOnlyList<RqlNode> Children) : RqlNode;

    /// <summary>Disjunction of two or more children; flattened like <see cref="And"/>.</summary>
    public sealed record Or(IReadOnlyList<RqlNode> Children) : RqlNode;

    /// <summary>
    /// Negation of one child.
    ///
    /// <para><b>This is not the NOT design.md §6.3 forbids.</b> That prohibition is about
    /// <i>access rules</i>, where a negation turns an allow-list into a deny-list and lets
    /// a missing attribute widen access. RQL's NOT negates a <i>content</i> condition over
    /// the candidate set only. It cannot widen access by construction: the candidate set
    /// is filtered by <c>canView</c> afterwards, per page, and no RQL expression is an
    /// input to that filter (design.md §22, "the query decides which candidates are
    /// considered; it never decides which permission check runs").</para>
    /// </summary>
    public sealed record Not(RqlNode Child) : RqlNode;

    /// <summary>One <c>field op value</c> comparison. <see cref="Values"/> is empty for
    /// <c>IS [NOT] EMPTY</c>, one element for the binary operators, and one-or-more for
    /// <c>IN</c> / <c>NOT IN</c>.</summary>
    public sealed record Predicate(
        RqlField Field, RqlOperator Operator, IReadOnlyList<RqlValue> Values, RqlSpan Span) : RqlNode;
}

/// <summary>
/// A value on the right-hand side of a predicate, after validation has decided what kind
/// of thing the field wanted. Closed hierarchy for the same reason as <see cref="RqlNode"/>.
/// </summary>
public abstract record RqlValue
{
    private RqlValue()
    {
    }

    /// <summary>The span of the value in the source, for error underlining.</summary>
    public RqlSpan Span { get; init; }

    /// <summary>A label name, space key, title fragment, or user subject.</summary>
    public sealed record Text(string Value) : RqlValue;

    /// <summary>
    /// A resolved point or day on the timeline. <see cref="EndExclusiveUtc"/> is non-null
    /// exactly when the author wrote a <b>date without a time</b>, in which case the value
    /// denotes the whole UTC day <c>[StartUtc, EndExclusiveUtc)</c> — so
    /// <c>created = "2026-01-31"</c> means "created some time that day" rather than "created
    /// at exactly midnight", which is what every author means and what no page row would
    /// ever match. An instant (<c>"2026-01-31T09:15:00Z"</c>, or any <c>now()</c>) has a
    /// null end and compares exactly.
    /// </summary>
    public sealed record Date(DateTime StartUtc, DateTime? EndExclusiveUtc, string CanonicalText) : RqlValue;

    /// <summary><c>currentUser()</c> — resolved against the request Principal at execution, never at parse.</summary>
    public sealed record CurrentUser : RqlValue;

    /// <summary>
    /// <c>now()</c> or <c>now("-7d")</c>. <see cref="Offset"/> is null for a bare
    /// <c>now()</c> and otherwise the sign-normalized offset (<c>"-7d"</c>, <c>"+1h"</c>).
    /// Resolved to a <see cref="Date"/> by <see cref="RqlNowResolver"/> — once per query
    /// execution, so two <c>now()</c>s in one query can never disagree.
    /// </summary>
    public sealed record Now(string? Offset) : RqlValue;
}
