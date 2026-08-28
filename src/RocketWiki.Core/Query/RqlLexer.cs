using System.Globalization;
using System.Text;

namespace RocketWiki.Core.Query;

internal enum RqlTokenKind
{
    /// <summary>A bare word: a field name, a function name, or an unquoted value.</summary>
    Word,

    /// <summary>A double-quoted string. <see cref="RqlToken.Text"/> is the unescaped content.</summary>
    QuotedString,

    And,
    Or,
    Not,
    In,
    Is,
    Empty,
    Order,
    By,
    Asc,
    Desc,

    Equals,
    NotEquals,
    Contains,
    NotContains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,

    LeftParen,
    RightParen,
    Comma,

    EndOfInput,
}

/// <summary>One token plus the exact source span it came from (quotes included, for a string).</summary>
internal readonly record struct RqlToken(RqlTokenKind Kind, string Text, RqlSpan Span)
{
    public bool IsValueToken => Kind is RqlTokenKind.Word or RqlTokenKind.QuotedString;
}

/// <summary>
/// Turns a query string into tokens (design.md §22's grammar). Pure, allocation-modest,
/// and total: it never throws on user input — every problem becomes a positioned
/// <see cref="RqlError"/> and lexing stops there.
///
/// <para><b>Keywords are keywords everywhere.</b> <c>AND</c>, <c>OR</c>, <c>NOT</c>,
/// <c>IN</c>, <c>IS</c>, <c>EMPTY</c>, <c>ORDER</c>, <c>BY</c>, <c>ASC</c> and <c>DESC</c>
/// are recognized case-insensitively wherever they appear, including where a value could
/// have gone. That makes the grammar unambiguous with no lookahead games, at the cost of
/// one documented rule: a value that <i>is</i> one of those words must be quoted
/// (<c>label = "in"</c>). The alternative — context-sensitive keywords — buys nothing and
/// is exactly how query languages grow corners nobody can reason about.</para>
/// </summary>
internal static class RqlLexer
{
    /// <summary>
    /// Characters allowed in a bare, unquoted token. Letters and digits, plus the
    /// punctuation an ISO-8601 instant, a hyphenated label, or a subject id needs
    /// (<c>2026-01-31T09:15:00+01:00</c>, <c>flight-ops</c>, <c>a.b@example.test</c>).
    /// Anything else has to be quoted, which is the escape hatch for every remaining case.
    /// </summary>
    private static bool IsBareWordChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '+' or '/' or '@';

    public static (IReadOnlyList<RqlToken> Tokens, IReadOnlyList<RqlError> Errors) Tokenize(string input)
    {
        var tokens = new List<RqlToken>();
        var errors = new List<RqlError>();
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (tokens.Count >= RqlLimits.MaxTokens)
            {
                errors.Add(new RqlError(
                    RqlErrorCode.TooComplex,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Query is too complex: more than {0} tokens.",
                        RqlLimits.MaxTokens),
                    new RqlSpan(i, input.Length - i)));
                return (tokens, errors);
            }

            var start = i;

            switch (c)
            {
                case '(':
                    tokens.Add(new RqlToken(RqlTokenKind.LeftParen, "(", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new RqlToken(RqlTokenKind.RightParen, ")", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new RqlToken(RqlTokenKind.Comma, ",", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case '=':
                    tokens.Add(new RqlToken(RqlTokenKind.Equals, "=", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case '~':
                    tokens.Add(new RqlToken(RqlTokenKind.Contains, "~", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case '!':
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new RqlToken(RqlTokenKind.NotEquals, "!=", new RqlSpan(start, 2)));
                        i += 2;
                        continue;
                    }

                    if (i + 1 < input.Length && input[i + 1] == '~')
                    {
                        tokens.Add(new RqlToken(RqlTokenKind.NotContains, "!~", new RqlSpan(start, 2)));
                        i += 2;
                        continue;
                    }

                    errors.Add(Unexpected(input, i, "Expected '!=' or '!~'."));
                    return (tokens, errors);
                case '>':
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new RqlToken(RqlTokenKind.GreaterThanOrEqual, ">=", new RqlSpan(start, 2)));
                        i += 2;
                        continue;
                    }

                    tokens.Add(new RqlToken(RqlTokenKind.GreaterThan, ">", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case '<':
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new RqlToken(RqlTokenKind.LessThanOrEqual, "<=", new RqlSpan(start, 2)));
                        i += 2;
                        continue;
                    }

                    tokens.Add(new RqlToken(RqlTokenKind.LessThan, "<", new RqlSpan(start, 1)));
                    i++;
                    continue;
                case '"':
                    if (!TryReadQuotedString(input, ref i, out var text, out var stringError))
                    {
                        errors.Add(stringError!);
                        return (tokens, errors);
                    }

                    tokens.Add(new RqlToken(RqlTokenKind.QuotedString, text!, new RqlSpan(start, i - start)));
                    continue;
            }

            if (IsBareWordChar(c))
            {
                while (i < input.Length && IsBareWordChar(input[i]))
                {
                    i++;
                }

                var word = input[start..i];
                tokens.Add(new RqlToken(KeywordKind(word), word, new RqlSpan(start, i - start)));
                continue;
            }

            errors.Add(Unexpected(input, i, null));
            return (tokens, errors);
        }

        tokens.Add(new RqlToken(RqlTokenKind.EndOfInput, string.Empty, new RqlSpan(input.Length, 0)));
        return (tokens, errors);
    }

    private static RqlError Unexpected(string input, int index, string? hint)
    {
        var message = string.Format(
            CultureInfo.InvariantCulture,
            "Unexpected character '{0}'.",
            input[index]);
        return new RqlError(RqlErrorCode.Syntax, hint is null ? message : message + " " + hint, new RqlSpan(index, 1));
    }

    private static RqlTokenKind KeywordKind(string word) => word.ToUpperInvariant() switch
    {
        "AND" => RqlTokenKind.And,
        "OR" => RqlTokenKind.Or,
        "NOT" => RqlTokenKind.Not,
        "IN" => RqlTokenKind.In,
        "IS" => RqlTokenKind.Is,
        "EMPTY" => RqlTokenKind.Empty,
        "ORDER" => RqlTokenKind.Order,
        "BY" => RqlTokenKind.By,
        "ASC" => RqlTokenKind.Asc,
        "DESC" => RqlTokenKind.Desc,
        _ => RqlTokenKind.Word,
    };

    /// <summary>
    /// Reads a double-quoted string with backslash escapes. Escapes are a closed set
    /// (<c>\" \\ \n \r \t</c>) and an unrecognized one is an error rather than a silent
    /// pass-through — a query language whose escape rules are "whatever the character
    /// after the backslash was" is one whose values differ between the printer and the
    /// parser, which would break the round trip the builder UI depends on.
    /// </summary>
    private static bool TryReadQuotedString(string input, ref int index, out string? text, out RqlError? error)
    {
        var start = index;
        index++; // opening quote
        var builder = new StringBuilder();

        while (index < input.Length)
        {
            var c = input[index];

            if (c == '"')
            {
                index++;
                text = builder.ToString();
                error = null;
                return true;
            }

            if (c != '\\')
            {
                builder.Append(c);
                index++;
                continue;
            }

            if (index + 1 >= input.Length)
            {
                break; // trailing backslash: falls through to the unterminated-string error
            }

            var escaped = input[index + 1];
            var replacement = escaped switch
            {
                '"' => '"',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => '\0',
            };

            if (replacement == '\0')
            {
                text = null;
                error = new RqlError(
                    RqlErrorCode.Syntax,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Unknown escape sequence '\\{0}'. Valid escapes are \\\" \\\\ \\n \\r and \\t.",
                        escaped),
                    new RqlSpan(index, 2));
                return false;
            }

            builder.Append(replacement);
            index += 2;
        }

        text = null;
        error = new RqlError(
            RqlErrorCode.Syntax,
            "Unterminated quoted value: no closing '\"'.",
            new RqlSpan(start, input.Length - start));
        return false;
    }
}
