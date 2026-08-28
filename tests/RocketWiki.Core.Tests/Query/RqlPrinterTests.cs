using RocketWiki.Core.Query;
using Xunit;

namespace RocketWiki.Core.Tests.Query;

/// <summary>
/// The canonical printer (design.md §22). The property that matters is that
/// <c>Print(Parse(x))</c> is a <b>fixed point</b>: the builder UI builds structurally, prints,
/// stores the string, and reparses it, so a printer whose output parsed to something that
/// printed differently would make a query drift a little every time it was edited.
/// </summary>
public class RqlPrinterTests
{
    /// <summary>
    /// The round-trip corpus. Deliberately includes the awkward cases — redundant
    /// parentheses, mixed precedence, NOT in every position, escapes, bare tokens, keyword
    /// casing, every operator form, both date precisions, both functions, ORDER BY with and
    /// without directions.
    /// </summary>
    public static TheoryData<string> Corpus =>
    [
        """label = "a" """,
        """label != "a" """,
        """label IN ("a", "b", "c")""",
        """label NOT IN ("a", "b")""",
        "label IS EMPTY",
        "label IS NOT EMPTY",
        """space = "ENG" """,
        "space = ENG",
        """space IN (ENG, OPS)""",
        """title ~ "turbo pump" """,
        """title !~ "draft" """,
        """title = "Exact Title" """,
        """created >= "2026-01-31" """,
        """created < "2026-01-31T09:15:00Z" """,
        """updated > now("-7d")""",
        """updated <= now()""",
        """updated > now("7d")""",
        "creator = currentUser()",
        """creator != "sub-1" """,
        """label = "a" AND space = "ENG" """,
        """label = "a" OR space = "ENG" """,
        """label = "a" AND label = "b" AND label = "c" """,
        """(label = "a" AND label = "b") AND label = "c" """,
        """label = "a" OR label = "b" AND label = "c" """,
        """(label = "a" OR label = "b") AND label = "c" """,
        """NOT label = "a" """,
        """NOT (label = "a" OR label = "b")""",
        """NOT (NOT label = "a")""",
        """NOT label = "a" AND space = "ENG" """,
        """title ~ "a \"quoted\" \\ value" """,
        """title ~ "line\nbreak\ttab" """,
        """title ~ "100% _ [bracket]" """,
        """label = "a" ORDER BY updated DESC""",
        """label = "a" ORDER BY title""",
        """label = "a" ORDER BY created ASC, title DESC""",
        """sPaCe = "ENG" aNd TITLE ~ "x" oRdEr By UPDATED desc""",
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void PrintParse_IsAFixedPoint(string source)
    {
        var first = Rql.Parse(source);
        Assert.True(first.IsValid, string.Join(" | ", first.Errors.Select(e => e.Message)));

        var second = Rql.Parse(first.Canonical!);
        Assert.True(second.IsValid, string.Join(" | ", second.Errors.Select(e => e.Message)));

        Assert.Equal(first.Canonical, second.Canonical);

        // And a third pass, because "stable after one round trip" is the bug this catches:
        // a printer can normalize once and then oscillate.
        Assert.Equal(first.Canonical, Rql.Parse(second.Canonical!).Canonical);
    }

    [Theory]
    [InlineData("space = ENG", """space = "ENG" """)]
    [InlineData("""  label   =   "a"   AND   space = "ENG"  """, """label = "a" AND space = "ENG" """)]
    [InlineData("""sPaCe = "ENG" aNd title ~ "x" """, """space = "ENG" AND title ~ "x" """)]
    [InlineData("""label = "a" ORDER BY title""", """label = "a" ORDER BY title ASC""")]
    [InlineData("""updated > now("7d")""", """updated > now("+7d")""")]
    [InlineData("""created = "2026-01-31T09:15:00+01:00" """, """created = "2026-01-31T08:15:00Z" """)]
    [InlineData("""label NOT IN (a,b)""", """label NOT IN ("a", "b")""")]
    [InlineData("label IS NOT EMPTY", "label IS NOT EMPTY")]
    public void Canonicalization_NormalizesSpelling(string source, string expected)
    {
        Assert.Equal(expected.TrimEnd(), Rql.Parse(source).Canonical);
    }

    [Theory]
    // Parentheses are emitted only where precedence needs them.
    [InlineData("""(label = "a") AND (space = "ENG")""", """label = "a" AND space = "ENG" """)]
    [InlineData("""(label = "a" AND label = "b") AND label = "c" """, """label = "a" AND label = "b" AND label = "c" """)]
    [InlineData("""label = "a" OR (label = "b" AND label = "c")""", """label = "a" OR label = "b" AND label = "c" """)]
    // ...and always where it does.
    [InlineData("""(label = "a" OR label = "b") AND label = "c" """, """(label = "a" OR label = "b") AND label = "c" """)]
    [InlineData("""NOT (label = "a" AND label = "b")""", """NOT (label = "a" AND label = "b")""")]
    public void Parentheses_AreMinimalButSufficient(string source, string expected)
    {
        Assert.Equal(expected.TrimEnd(), Rql.Parse(source).Canonical);
    }

    [Fact]
    public void ValuesAreAlwaysQuotedAndEscaped_SoNoValueCanBeRelexedAsSyntax()
    {
        // A value spelling a keyword, an operator, or a quote round-trips because the
        // printer never emits a bare token.
        foreach (var value in new[] { "AND", "OR", "NOT IN", "= x", "a\"b", "a\\b", "a\nb", "(x)" })
        {
            var source = new RqlQuery(
                new RqlNode.Predicate(RqlField.Title, RqlOperator.Equals, [new RqlValue.Text(value)], RqlSpan.Empty),
                []);

            var printed = Rql.Print(source);
            var reparsed = Rql.Parse(printed);

            Assert.True(reparsed.IsValid, printed + " => " + string.Join(" | ", reparsed.Errors.Select(e => e.Message)));
            var predicate = Assert.IsType<RqlNode.Predicate>(reparsed.Query!.Where);
            Assert.Equal(value, Assert.IsType<RqlValue.Text>(predicate.Values[0]).Value);
        }
    }

    [Fact]
    public void OrderByIsNotBackFilledWithTheExecutionDefault()
    {
        // The default (updated DESC) belongs to execution. Materializing it here would make
        // a query grow an ORDER BY clause merely by passing through the builder.
        Assert.Equal("""label = "a" """.TrimEnd(), Rql.Parse("""label = "a" """).Canonical);
    }
}
