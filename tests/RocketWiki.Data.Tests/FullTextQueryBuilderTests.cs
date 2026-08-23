using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Pure-logic tier for the SQL Server CONTAINS condition builder (design.md §14 tier 1:
/// no database needed to prove tokenizing/escaping). Whether SQL Server actually
/// *accepts* these conditions and stems them is proven where it can only be proven -
/// tests/RocketWiki.SqlServer.Tests's FullTextSearchTests against a real FTS-enabled
/// container.
/// </summary>
public class FullTextQueryBuilderTests
{
    [Fact]
    public void SingleWord_BecomesOneInflectionalTerm()
    {
        Assert.Equal(
            "FORMSOF(INFLECTIONAL, \"running\")",
            FullTextQueryBuilder.BuildContainsCondition("running"));
    }

    [Fact]
    public void MultipleWords_AreAnded_NotPassedRaw()
    {
        // Raw "rocket engine" is a CONTAINSTABLE syntax error - the builder must split.
        Assert.Equal(
            "FORMSOF(INFLECTIONAL, \"rocket\") AND FORMSOF(INFLECTIONAL, \"engine\")",
            FullTextQueryBuilder.BuildContainsCondition("rocket engine"));
    }

    [Fact]
    public void PunctuationAndQuotes_AreTermSeparators_SoNoQuoteCanEscapeTheTerm()
    {
        // Double quotes delimit terms in the CONTAINS grammar; the tokenizer treats
        // them (and every other non-alphanumeric) as separators, so user input can
        // never break out of the quoted term the builder emits.
        Assert.Equal(
            "FORMSOF(INFLECTIONAL, \"impeller\") AND FORMSOF(INFLECTIONAL, \"cavitation\")",
            FullTextQueryBuilder.BuildContainsCondition("impeller-cavitation"));
        // An injection attempt: quotes and parens vanish as separators, and the OR in
        // the middle survives only as a *quoted term* - a literal word to search for,
        // never an operator.
        Assert.Equal(
            "FORMSOF(INFLECTIONAL, \"a\") AND FORMSOF(INFLECTIONAL, \"OR\") AND FORMSOF(INFLECTIONAL, \"b\")",
            FullTextQueryBuilder.BuildContainsCondition("\"a\") OR (b"));
    }

    [Fact]
    public void NoIndexableTerms_ReturnsNull()
    {
        Assert.Null(FullTextQueryBuilder.BuildContainsCondition("--- !!! ..."));
        Assert.Null(FullTextQueryBuilder.BuildContainsCondition("   "));
    }

    [Fact]
    public void TermCount_IsBounded()
    {
        var query = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"term{i}"));

        var condition = FullTextQueryBuilder.BuildContainsCondition(query);

        Assert.NotNull(condition);
        Assert.Equal(16, condition!.Split(" AND ").Length);
    }

    [Fact]
    public void UnicodeLetters_SurviveTokenizing()
    {
        Assert.Equal(
            "FORMSOF(INFLECTIONAL, \"trägheitsnavigation\")",
            FullTextQueryBuilder.BuildContainsCondition("trägheitsnavigation"));
    }
}
