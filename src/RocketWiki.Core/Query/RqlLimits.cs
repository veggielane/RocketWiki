namespace RocketWiki.Core.Query;

/// <summary>
/// Hard caps on the shape of a query (design.md §22, "bounded cost"). All of them are
/// enforced during parse/validate, so a pathological query is refused with a positioned
/// validation error before it can reach the lexer's inner loop, the parser's stack, or
/// the database.
///
/// <para>These are deliberately small. RQL is a filter for a page-listing widget, not a
/// reporting language; every one of these limits is an order of magnitude above what a
/// hand-written query needs and well below what would cost anything to evaluate.</para>
/// </summary>
public static class RqlLimits
{
    /// <summary>Longest accepted query string. Checked before tokenizing, so an
    /// adversarially long string is rejected in O(1).</summary>
    public const int MaxQueryLength = 4096;

    /// <summary>Upper bound on tokens, so a query of nothing but punctuation inside
    /// <see cref="MaxQueryLength"/> still terminates the parser quickly.</summary>
    public const int MaxTokens = 512;

    /// <summary>Upper bound on predicates in one query.</summary>
    public const int MaxPredicates = 32;

    /// <summary>
    /// Maximum nesting depth of the boolean tree. Enforced <b>inside</b> the recursive
    /// descent rather than on the finished AST: a thousand nested parentheses would
    /// overflow the parser's own stack long before there was a tree to measure.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>Maximum values in one <c>IN</c> / <c>NOT IN</c> list.</summary>
    public const int MaxValuesPerPredicate = 50;

    /// <summary>Maximum ORDER BY terms — there are only three sortable fields.</summary>
    public const int MaxOrderItems = 3;
}
