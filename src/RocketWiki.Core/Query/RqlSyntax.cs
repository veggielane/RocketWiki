namespace RocketWiki.Core.Query;

/// <summary>
/// A half-open range of the source query string, in UTF-16 code units from the start of
/// the string the user typed. Every AST node and every <see cref="RqlError"/> carries one
/// so the SPA can underline exactly the offending run of text (design.md §22).
/// </summary>
public readonly record struct RqlSpan(int Offset, int Length)
{
    public static RqlSpan Empty => new(0, 0);

    /// <summary>The span covering <paramref name="first"/> through <paramref name="last"/> inclusive.</summary>
    public static RqlSpan Cover(RqlSpan first, RqlSpan last) =>
        new(first.Offset, Math.Max((last.Offset + last.Length) - first.Offset, 0));
}

/// <summary>
/// The CLOSED set of queryable fields (design.md §22). Adding a member here is a
/// deliberate widening of what an author may filter on and must be argued for in §22 —
/// in particular, nothing describing classification, clearance, or permission state may
/// ever appear (see <see cref="RqlVocabulary"/>, and §21.8 for why).
/// </summary>
public enum RqlField
{
    Label,
    Space,
    Title,
    Created,
    Updated,
    Creator,
}

/// <summary>Comparison operators. Which ones a given field accepts is <see cref="RqlVocabulary"/>'s decision, not the parser's.</summary>
public enum RqlOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In,
    NotIn,
    IsEmpty,
    IsNotEmpty,
}

public enum RqlSortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// The bounded vocabulary of things that can go wrong, so a client can branch on the
/// kind of mistake without matching on message text. Deliberately distinguishes
/// <see cref="NotQueryableField"/> (classification/permission state — the author is
/// being taught a rule) from <see cref="UnknownField"/> (a typo) and
/// <see cref="UnsupportedField"/> (a real field that has not shipped yet): collapsing
/// the three would make every one of them read as "you typed it wrong".
/// </summary>
public enum RqlErrorCode
{
    Syntax,
    UnknownField,
    NotQueryableField,
    UnsupportedField,
    OperatorNotAllowed,
    InvalidValue,
    UnknownFunction,
    FunctionNotAllowedHere,
    TooComplex,
}
