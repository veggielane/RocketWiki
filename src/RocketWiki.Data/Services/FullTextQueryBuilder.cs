using System.Text;

namespace RocketWiki.Data.Services;

/// <summary>
/// Turns a user's raw search string into a SQL Server CONTAINS/CONTAINSTABLE search
/// condition. The raw string must never reach CONTAINSTABLE directly: the search
/// condition is its own mini-language, and any query with a space, hyphen, or
/// punctuation ("rocket engine", "impeller-cavitation") is a *syntax error* there,
/// not a search. So the query is reduced to its alphanumeric terms, each term wrapped
/// in FORMSOF(INFLECTIONAL, ...) - which is also what makes FTS behave like search
/// rather than LIKE: "running" matches a page that only ever says "run" (word-breaker
/// stemming, design.md §9.1), something the SQLite tier's LIKE fallback can never do.
/// Terms are AND-ed, mirroring the LIKE fallback's all-terms-present substring
/// semantics as closely as a word-based index can.
///
/// This is a pure function on purpose: the SQLite-backed unit tier proves the
/// condition-building logic (escaping, tokenizing, the no-terms case), while
/// tests/RocketWiki.SqlServer.Tests proves that SQL Server actually accepts and
/// stems the conditions built here. Values are still passed to CONTAINSTABLE as a
/// parameter - this builder constructs the condition *value*, never SQL text.
/// </summary>
public static class FullTextQueryBuilder
{
    /// <summary>
    /// Upper bound on terms sent to the index. Anything beyond this in one query is
    /// noise, and unbounded user input should not become an unbounded FTS condition.
    /// </summary>
    private const int MaxTerms = 16;

    /// <summary>
    /// Builds the CONTAINS search condition for <paramref name="query"/>, or null when
    /// the query contains no indexable term at all (e.g. only punctuation) - callers
    /// treat null as "no keyword candidates", matching what LIKE '%--%' would find
    /// against word-broken content: nothing meaningful.
    /// </summary>
    public static string? BuildContainsCondition(string query)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var term in terms)
        {
            if (builder.Length > 0)
            {
                builder.Append(" AND ");
            }

            // Quoted so a term is always a <simple_term> to the parser; tokenizing on
            // non-alphanumerics already guarantees no embedded quote can break out.
            builder.Append("FORMSOF(INFLECTIONAL, \"").Append(term).Append("\")");
        }

        return builder.ToString();
    }

    private static List<string> Tokenize(string query)
    {
        var terms = new List<string>();
        var current = new StringBuilder();

        foreach (var c in query)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            AppendTerm(terms, current);
            if (terms.Count == MaxTerms)
            {
                return terms;
            }
        }

        AppendTerm(terms, current);
        return terms;
    }

    private static void AppendTerm(List<string> terms, StringBuilder current)
    {
        if (current.Length > 0 && terms.Count < MaxTerms)
        {
            terms.Add(current.ToString());
        }

        current.Clear();
    }
}
