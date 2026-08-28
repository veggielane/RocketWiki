using System.Text;

namespace RocketWiki.Core.Query;

/// <summary>
/// Renders a validated <see cref="RqlQuery"/> back to its canonical string (design.md §22).
///
/// <para><b>Why a printer exists at all.</b> The string is the canonical form — it is what
/// an author types, what a Markdown fence stores, and what the GraphQL field accepts — so a
/// structural builder UI cannot own a second representation. It builds structurally, prints
/// through here, stores the string, and reparses it. That round trip is only trustworthy if
/// it is a fixed point: <c>Print(Parse(Print(Parse(x)))) == Print(Parse(x))</c>, pinned by
/// test over a corpus.</para>
///
/// <para>Three rules make it one. Keywords are always upper case. Values are
/// <b>always</b> double-quoted and escaped, so no value can ever be re-lexed as a keyword or
/// an operator and there is no "is this safe bare?" judgement to get subtly wrong. And
/// parentheses are emitted only where precedence requires them, which — together with the
/// parser flattening same-operator chains — means a redundantly parenthesized query and its
/// bare equivalent print identically.</para>
/// </summary>
public static class RqlPrinter
{
    private const int OrPrecedence = 1;
    private const int AndPrecedence = 2;
    private const int NotPrecedence = 3;
    private const int PredicatePrecedence = 4;

    public static string Print(RqlQuery query)
    {
        var builder = new StringBuilder();
        Write(builder, query.Where, OrPrecedence);

        if (query.OrderBy.Count > 0)
        {
            builder.Append(" ORDER BY ");
            for (var i = 0; i < query.OrderBy.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                var item = query.OrderBy[i];
                builder
                    .Append(RqlVocabulary.NameOf(item.Field))
                    .Append(item.Direction == RqlSortDirection.Descending ? " DESC" : " ASC");
            }
        }

        return builder.ToString();
    }

    private static int PrecedenceOf(RqlNode node) => node switch
    {
        RqlNode.Or => OrPrecedence,
        RqlNode.And => AndPrecedence,
        RqlNode.Not => NotPrecedence,
        RqlNode.Predicate => PredicatePrecedence,
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unhandled RQL node."),
    };

    private static void Write(StringBuilder builder, RqlNode node, int minimumPrecedence)
    {
        var parenthesize = PrecedenceOf(node) < minimumPrecedence;
        if (parenthesize)
        {
            builder.Append('(');
        }

        switch (node)
        {
            case RqlNode.Or or:
                WriteChildren(builder, or.Children, " OR ", AndPrecedence);
                break;

            case RqlNode.And and:
                WriteChildren(builder, and.Children, " AND ", AndPrecedence);
                break;

            case RqlNode.Not not:
                builder.Append("NOT ");
                // Only a bare predicate can follow NOT unparenthesized: `NOT a AND b` parses
                // as `(NOT a) AND b`, and `NOT NOT a` does not parse at all.
                Write(builder, not.Child, PredicatePrecedence);
                break;

            case RqlNode.Predicate predicate:
                WritePredicate(builder, predicate);
                break;
        }

        if (parenthesize)
        {
            builder.Append(')');
        }
    }

    private static void WriteChildren(
        StringBuilder builder, IReadOnlyList<RqlNode> children, string separator, int childMinimumPrecedence)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            Write(builder, children[i], childMinimumPrecedence);
        }
    }

    private static void WritePredicate(StringBuilder builder, RqlNode.Predicate predicate)
    {
        builder.Append(RqlVocabulary.NameOf(predicate.Field)).Append(' ').Append(RqlVocabulary.NameOf(predicate.Operator));

        if (predicate.Operator is RqlOperator.IsEmpty or RqlOperator.IsNotEmpty)
        {
            return;
        }

        if (predicate.Operator is RqlOperator.In or RqlOperator.NotIn)
        {
            builder.Append(" (");
            for (var i = 0; i < predicate.Values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                WriteValue(builder, predicate.Values[i]);
            }

            builder.Append(')');
            return;
        }

        builder.Append(' ');
        WriteValue(builder, predicate.Values[0]);
    }

    private static void WriteValue(StringBuilder builder, RqlValue value)
    {
        switch (value)
        {
            case RqlValue.Text text:
                WriteQuoted(builder, text.Value);
                break;

            case RqlValue.Date date:
                WriteQuoted(builder, date.CanonicalText);
                break;

            case RqlValue.CurrentUser:
                builder.Append("currentUser()");
                break;

            case RqlValue.Now now:
                builder.Append("now(");
                if (now.Offset is not null)
                {
                    WriteQuoted(builder, now.Offset);
                }

                builder.Append(')');
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled RQL value.");
        }
    }

    /// <summary>Mirrors <see cref="RqlLexer"/>'s closed escape set exactly — if the two
    /// ever disagree, a value survives one round trip and changes on the next.</summary>
    private static void WriteQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }
}
