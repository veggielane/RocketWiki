using System.Globalization;

namespace RocketWiki.Core.Query;

/// <summary>A node of the untyped tree the parser produces: shape only, vocabulary unchecked.</summary>
internal abstract record RawNode(RqlSpan Span);

internal sealed record RawGroup(bool IsOr, IReadOnlyList<RawNode> Children, RqlSpan Span) : RawNode(Span);

internal sealed record RawNot(RawNode Child, RqlSpan Span) : RawNode(Span);

internal sealed record RawPredicate(
    string FieldName,
    RqlSpan FieldSpan,
    RqlOperator Operator,
    RqlSpan OperatorSpan,
    IReadOnlyList<RawValue> Values,
    RqlSpan Span) : RawNode(Span);

internal abstract record RawValue(RqlSpan Span);

internal sealed record RawLiteral(string Text, RqlSpan Span) : RawValue(Span);

/// <summary><c>name(...)</c>. <see cref="Argument"/> is null for a no-argument call; arity is the validator's business, not the parser's.</summary>
internal sealed record RawFunction(string Name, string? Argument, RqlSpan ArgumentSpan, RqlSpan Span) : RawValue(Span);

internal sealed record RawOrderItem(string FieldName, RqlSpan FieldSpan, RqlSortDirection Direction, RqlSpan Span);

internal sealed record RawQuery(RawNode Where, IReadOnlyList<RawOrderItem> OrderBy);

/// <summary>
/// Recursive-descent parser for design.md §22's grammar. Produces an untyped tree: it
/// enforces <i>shape</i> (precedence, associativity, parentheses, operator forms) and
/// leaves every question of vocabulary — is <c>marking</c> a field, does <c>label</c>
/// accept <c>~</c>, is <c>"tomorrow"</c> a date — to <see cref="RqlValidator"/>.
///
/// <para>The split is what lets the validator report <b>every</b> vocabulary mistake in one
/// pass with positions (the SPA underlines them all at once) instead of stopping at the
/// first. Syntax errors do stop at the first: past a shape error the token stream means
/// nothing, and guessing at a repair produces cascades of invented errors pointing at text
/// the author never wrote.</para>
///
/// <para>Depth is bounded <b>during</b> the descent (<see cref="RqlLimits.MaxDepth"/>), not
/// on the finished tree, because a thousand nested parentheses would overflow this parser's
/// own stack long before there was a tree to inspect.</para>
/// </summary>
internal sealed class RqlParser
{
    private readonly IReadOnlyList<RqlToken> _tokens;
    private readonly List<RqlError> _errors = [];
    private int _index;
    private int _depth;
    private int _predicateCount;

    private RqlParser(IReadOnlyList<RqlToken> tokens) => _tokens = tokens;

    public static (RawQuery? Query, IReadOnlyList<RqlError> Errors) Parse(IReadOnlyList<RqlToken> tokens)
    {
        var parser = new RqlParser(tokens);
        var query = parser.ParseQuery();
        return parser._errors.Count > 0 ? (null, parser._errors) : (query, parser._errors);
    }

    private RqlToken Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

    private RqlToken Peek(int ahead = 1) => _tokens[Math.Min(_index + ahead, _tokens.Count - 1)];

    private bool Failed => _errors.Count > 0;

    private void Fail(RqlErrorCode code, string message, RqlSpan span)
    {
        if (!Failed)
        {
            _errors.Add(new RqlError(code, message, span));
        }
    }

    private void FailAt(RqlToken token, string expected) => Fail(
        RqlErrorCode.Syntax,
        string.Format(
            CultureInfo.InvariantCulture,
            token.Kind == RqlTokenKind.EndOfInput ? "Unexpected end of query. {0}" : "Unexpected '{1}'. {0}",
            expected,
            token.Text),
        token.Span);

    private RawQuery? ParseQuery()
    {
        if (Current.Kind == RqlTokenKind.EndOfInput)
        {
            Fail(
                RqlErrorCode.Syntax,
                "A query must contain at least one condition, for example: space = \"ENG\".",
                Current.Span);
            return null;
        }

        var where = ParseOr();
        if (Failed || where is null)
        {
            return null;
        }

        var orderBy = ParseOrderBy();
        if (Failed)
        {
            return null;
        }

        if (Current.Kind != RqlTokenKind.EndOfInput)
        {
            FailAt(Current, "Expected the end of the query, AND, OR, or ORDER BY.");
            return null;
        }

        return new RawQuery(where, orderBy ?? []);
    }

    private RawNode? ParseOr()
    {
        if (++_depth > RqlLimits.MaxDepth)
        {
            Fail(
                RqlErrorCode.TooComplex,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Query is nested more than {0} levels deep.",
                    RqlLimits.MaxDepth),
                Current.Span);
            return null;
        }

        try
        {
            var first = ParseAnd();
            if (Failed || first is null)
            {
                return null;
            }

            if (Current.Kind != RqlTokenKind.Or)
            {
                return first;
            }

            var children = new List<RawNode>();
            AddFlattened(children, first, isOr: true);

            while (Current.Kind == RqlTokenKind.Or)
            {
                _index++;
                var next = ParseAnd();
                if (Failed || next is null)
                {
                    return null;
                }

                AddFlattened(children, next, isOr: true);
            }

            return new RawGroup(true, children, RqlSpan.Cover(first.Span, children[^1].Span));
        }
        finally
        {
            _depth--;
        }
    }

    private RawNode? ParseAnd()
    {
        var first = ParseNot();
        if (Failed || first is null)
        {
            return null;
        }

        if (Current.Kind != RqlTokenKind.And)
        {
            return first;
        }

        var children = new List<RawNode>();
        AddFlattened(children, first, isOr: false);

        while (Current.Kind == RqlTokenKind.And)
        {
            _index++;
            var next = ParseNot();
            if (Failed || next is null)
            {
                return null;
            }

            AddFlattened(children, next, isOr: false);
        }

        return new RawGroup(false, children, RqlSpan.Cover(first.Span, children[^1].Span));
    }

    /// <summary>
    /// Splices a same-kind child into its parent's child list, so <c>a AND b AND c</c> and
    /// the redundantly parenthesized <c>(a AND b) AND c</c> produce the identical tree.
    /// Canonicalizing here rather than in the printer is what makes the builder UI's
    /// build → print → store → reparse cycle a fixed point.
    /// </summary>
    private static void AddFlattened(List<RawNode> children, RawNode child, bool isOr)
    {
        if (child is RawGroup group && group.IsOr == isOr)
        {
            children.AddRange(group.Children);
            return;
        }

        children.Add(child);
    }

    private RawNode? ParseNot()
    {
        if (Current.Kind != RqlTokenKind.Not)
        {
            return ParsePrimary();
        }

        var notToken = Current;
        _index++;
        var child = ParsePrimary();
        if (Failed || child is null)
        {
            return null;
        }

        return new RawNot(child, RqlSpan.Cover(notToken.Span, child.Span));
    }

    private RawNode? ParsePrimary()
    {
        if (Current.Kind == RqlTokenKind.LeftParen)
        {
            var open = Current;
            _index++;
            var inner = ParseOr();
            if (Failed || inner is null)
            {
                return null;
            }

            if (Current.Kind != RqlTokenKind.RightParen)
            {
                FailAt(Current, "Expected ')'.");
                return null;
            }

            var close = Current;
            _index++;

            // The group's span covers the parentheses so an error inside a group can still
            // be underlined against the group the author sees.
            return inner switch
            {
                RawGroup g => g with { Span = RqlSpan.Cover(open.Span, close.Span) },
                RawNot n => n with { Span = RqlSpan.Cover(open.Span, close.Span) },
                _ => inner,
            };
        }

        return ParsePredicate();
    }

    private RawNode? ParsePredicate()
    {
        if (Current.Kind != RqlTokenKind.Word)
        {
            FailAt(Current, "Expected a field name.");
            return null;
        }

        if (++_predicateCount > RqlLimits.MaxPredicates)
        {
            Fail(
                RqlErrorCode.TooComplex,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Query contains more than {0} conditions.",
                    RqlLimits.MaxPredicates),
                Current.Span);
            return null;
        }

        var fieldToken = Current;
        _index++;

        switch (Current.Kind)
        {
            case RqlTokenKind.In:
                return ParseInPredicate(fieldToken, RqlOperator.In, Current.Span);

            case RqlTokenKind.Not when Peek().Kind == RqlTokenKind.In:
            {
                var notSpan = Current.Span;
                _index++; // NOT; ParseInPredicate consumes the IN
                return ParseInPredicate(fieldToken, RqlOperator.NotIn, RqlSpan.Cover(notSpan, Current.Span));
            }

            case RqlTokenKind.Is:
                return ParseIsEmptyPredicate(fieldToken);

            default:
            {
                if (!TryTakeComparisonOperator(out var op, out var opSpan))
                {
                    FailAt(Current, "Expected an operator (=, !=, ~, !~, >, >=, <, <=, IN, NOT IN, or IS [NOT] EMPTY).");
                    return null;
                }

                var value = ParseValue();
                if (Failed || value is null)
                {
                    return null;
                }

                return new RawPredicate(
                    fieldToken.Text, fieldToken.Span, op, opSpan, [value],
                    RqlSpan.Cover(fieldToken.Span, value.Span));
            }
        }
    }

    private bool TryTakeComparisonOperator(out RqlOperator op, out RqlSpan span)
    {
        op = Current.Kind switch
        {
            RqlTokenKind.Equals => RqlOperator.Equals,
            RqlTokenKind.NotEquals => RqlOperator.NotEquals,
            RqlTokenKind.Contains => RqlOperator.Contains,
            RqlTokenKind.NotContains => RqlOperator.NotContains,
            RqlTokenKind.GreaterThan => RqlOperator.GreaterThan,
            RqlTokenKind.GreaterThanOrEqual => RqlOperator.GreaterThanOrEqual,
            RqlTokenKind.LessThan => RqlOperator.LessThan,
            RqlTokenKind.LessThanOrEqual => RqlOperator.LessThanOrEqual,
            _ => (RqlOperator)(-1),
        };

        span = Current.Span;
        if ((int)op < 0)
        {
            return false;
        }

        _index++;
        return true;
    }

    private RawNode? ParseInPredicate(RqlToken fieldToken, RqlOperator op, RqlSpan opSpan)
    {
        _index++; // IN

        if (Current.Kind != RqlTokenKind.LeftParen)
        {
            FailAt(Current, "Expected '(' after IN.");
            return null;
        }

        _index++;
        var values = new List<RawValue>();

        while (true)
        {
            var value = ParseValue();
            if (Failed || value is null)
            {
                return null;
            }

            values.Add(value);

            if (values.Count > RqlLimits.MaxValuesPerPredicate)
            {
                Fail(
                    RqlErrorCode.TooComplex,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "An IN list may hold at most {0} values.",
                        RqlLimits.MaxValuesPerPredicate),
                    value.Span);
                return null;
            }

            if (Current.Kind == RqlTokenKind.Comma)
            {
                _index++;
                continue;
            }

            break;
        }

        if (Current.Kind != RqlTokenKind.RightParen)
        {
            FailAt(Current, "Expected ',' or ')' in the IN list.");
            return null;
        }

        var close = Current;
        _index++;

        return new RawPredicate(
            fieldToken.Text, fieldToken.Span, op, opSpan, values, RqlSpan.Cover(fieldToken.Span, close.Span));
    }

    private RawNode? ParseIsEmptyPredicate(RqlToken fieldToken)
    {
        var isSpan = Current.Span;
        _index++; // IS

        var op = RqlOperator.IsEmpty;
        if (Current.Kind == RqlTokenKind.Not)
        {
            op = RqlOperator.IsNotEmpty;
            _index++;
        }

        if (Current.Kind != RqlTokenKind.Empty)
        {
            FailAt(Current, "Expected EMPTY after IS or IS NOT.");
            return null;
        }

        var emptyToken = Current;
        _index++;

        return new RawPredicate(
            fieldToken.Text,
            fieldToken.Span,
            op,
            RqlSpan.Cover(isSpan, emptyToken.Span),
            [],
            RqlSpan.Cover(fieldToken.Span, emptyToken.Span));
    }

    private RawValue? ParseValue()
    {
        // A bare word immediately followed by '(' is a function call; anything else that
        // can hold text is a literal. Keywords are never values (see RqlLexer's doc) - a
        // value that spells one has to be quoted.
        if (Current.Kind == RqlTokenKind.Word && Peek().Kind == RqlTokenKind.LeftParen)
        {
            return ParseFunction();
        }

        if (!Current.IsValueToken)
        {
            FailAt(Current, "Expected a value: a bare word, a quoted string, currentUser(), or now().");
            return null;
        }

        var token = Current;
        _index++;
        return new RawLiteral(token.Text, token.Span);
    }

    private RawValue? ParseFunction()
    {
        var nameToken = Current;
        _index += 2; // name and '('

        string? argument = null;
        var argumentSpan = RqlSpan.Empty;

        if (Current.IsValueToken)
        {
            argument = Current.Text;
            argumentSpan = Current.Span;
            _index++;
        }

        if (Current.Kind != RqlTokenKind.RightParen)
        {
            FailAt(Current, "Expected ')' to close the function call.");
            return null;
        }

        var close = Current;
        _index++;

        return new RawFunction(
            nameToken.Text, argument, argumentSpan, RqlSpan.Cover(nameToken.Span, close.Span));
    }

    private List<RawOrderItem>? ParseOrderBy()
    {
        if (Current.Kind != RqlTokenKind.Order)
        {
            return [];
        }

        _index++;
        if (Current.Kind != RqlTokenKind.By)
        {
            FailAt(Current, "Expected BY after ORDER.");
            return null;
        }

        _index++;
        var items = new List<RawOrderItem>();

        while (true)
        {
            if (Current.Kind != RqlTokenKind.Word)
            {
                FailAt(Current, "Expected a field name to order by.");
                return null;
            }

            var fieldToken = Current;
            _index++;

            var direction = RqlSortDirection.Ascending;
            var span = fieldToken.Span;

            if (Current.Kind is RqlTokenKind.Asc or RqlTokenKind.Desc)
            {
                direction = Current.Kind == RqlTokenKind.Desc ? RqlSortDirection.Descending : RqlSortDirection.Ascending;
                span = RqlSpan.Cover(fieldToken.Span, Current.Span);
                _index++;
            }

            items.Add(new RawOrderItem(fieldToken.Text, fieldToken.Span, direction, span));

            if (items.Count > RqlLimits.MaxOrderItems)
            {
                Fail(
                    RqlErrorCode.TooComplex,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ORDER BY accepts at most {0} terms.",
                        RqlLimits.MaxOrderItems),
                    span);
                return null;
            }

            if (Current.Kind != RqlTokenKind.Comma)
            {
                return items;
            }

            _index++;
        }
    }
}
