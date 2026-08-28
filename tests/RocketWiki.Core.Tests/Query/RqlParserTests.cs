using RocketWiki.Core.Query;
using Xunit;

namespace RocketWiki.Core.Tests.Query;

/// <summary>
/// design.md §22's grammar, exercised on shape: precedence, associativity, parentheses,
/// quoting and escapes, every operator form, every field, ORDER BY, and the caps.
/// Malformed input is asserted <b>with its position</b>, because an unpositioned parse error
/// is useless to the editor this language exists to be typed into.
/// </summary>
public class RqlParserTests
{
    private static RqlQuery ParseOk(string text)
    {
        var result = Rql.Parse(text);
        Assert.True(
            result.IsValid,
            "Expected a valid parse but got: " + string.Join(" | ", result.Errors.Select(e => e.Message)));
        return result.Query!;
    }

    private static RqlError ParseError(string text)
    {
        var result = Rql.Parse(text);
        Assert.False(result.IsValid, "Expected a parse failure for: " + text);
        return result.Errors[0];
    }

    [Fact]
    public void And_BindsTighterThanOr()
    {
        var query = ParseOk("""space = "A" OR space = "B" AND title ~ "x" """);

        var or = Assert.IsType<RqlNode.Or>(query.Where);
        Assert.Equal(2, or.Children.Count);
        Assert.IsType<RqlNode.Predicate>(or.Children[0]);
        var and = Assert.IsType<RqlNode.And>(or.Children[1]);
        Assert.Equal(2, and.Children.Count);
    }

    [Fact]
    public void RepeatedOperators_FlattenIntoOneNaryNode()
    {
        var and = Assert.IsType<RqlNode.And>(
            ParseOk("""label = "a" AND label = "b" AND label = "c" """).Where);
        Assert.Equal(3, and.Children.Count);

        // Redundant parentheses produce the identical tree - the property the builder UI's
        // build -> print -> store -> reparse round trip rests on.
        var reparenthesized = Assert.IsType<RqlNode.And>(
            ParseOk("""(label = "a" AND label = "b") AND label = "c" """).Where);
        Assert.Equal(3, reparenthesized.Children.Count);

        var or = Assert.IsType<RqlNode.Or>(ParseOk("""label = "a" OR label = "b" OR label = "c" """).Where);
        Assert.Equal(3, or.Children.Count);
    }

    [Fact]
    public void Parentheses_OverridePrecedence()
    {
        var and = Assert.IsType<RqlNode.And>(
            ParseOk("""(space = "A" OR space = "B") AND title ~ "x" """).Where);
        Assert.Equal(2, and.Children.Count);
        Assert.IsType<RqlNode.Or>(and.Children[0]);
        Assert.IsType<RqlNode.Predicate>(and.Children[1]);
    }

    [Fact]
    public void Not_BindsToOnePrimary_NotToTheWholeConjunction()
    {
        var and = Assert.IsType<RqlNode.And>(ParseOk("""NOT label = "a" AND label = "b" """).Where);
        Assert.IsType<RqlNode.Not>(and.Children[0]);
        Assert.IsType<RqlNode.Predicate>(and.Children[1]);

        var not = Assert.IsType<RqlNode.Not>(ParseOk("""NOT (label = "a" AND label = "b")""").Where);
        Assert.IsType<RqlNode.And>(not.Child);
    }

    [Fact]
    public void Keywords_AreCaseInsensitive()
    {
        var query = ParseOk("""SPACE = "A" and Title ~ "x" Order By Updated Desc""");

        Assert.IsType<RqlNode.And>(query.Where);
        var order = Assert.Single(query.OrderBy);
        Assert.Equal(RqlField.Updated, order.Field);
        Assert.Equal(RqlSortDirection.Descending, order.Direction);
    }

    [Theory]
    [InlineData("""label = "x" """, RqlOperator.Equals)]
    [InlineData("""label != "x" """, RqlOperator.NotEquals)]
    [InlineData("""title ~ "x" """, RqlOperator.Contains)]
    [InlineData("""title !~ "x" """, RqlOperator.NotContains)]
    [InlineData("""created > "2026-01-31" """, RqlOperator.GreaterThan)]
    [InlineData("""created >= "2026-01-31" """, RqlOperator.GreaterThanOrEqual)]
    [InlineData("""created < "2026-01-31" """, RqlOperator.LessThan)]
    [InlineData("""created <= "2026-01-31" """, RqlOperator.LessThanOrEqual)]
    [InlineData("""label IN ("a", "b")""", RqlOperator.In)]
    [InlineData("""label NOT IN ("a", "b")""", RqlOperator.NotIn)]
    [InlineData("label IS EMPTY", RqlOperator.IsEmpty)]
    [InlineData("label IS NOT EMPTY", RqlOperator.IsNotEmpty)]
    public void EveryOperatorForm_Parses(string text, RqlOperator expected)
    {
        var predicate = Assert.IsType<RqlNode.Predicate>(ParseOk(text).Where);
        Assert.Equal(expected, predicate.Operator);
    }

    [Theory]
    [InlineData("""label = "a" """, RqlField.Label)]
    [InlineData("""space = "ENG" """, RqlField.Space)]
    [InlineData("""title = "T" """, RqlField.Title)]
    [InlineData("""created = "2026-01-31" """, RqlField.Created)]
    [InlineData("""updated = "2026-01-31" """, RqlField.Updated)]
    [InlineData("""creator = "sub-1" """, RqlField.Creator)]
    public void EveryField_Parses(string text, RqlField expected)
    {
        var predicate = Assert.IsType<RqlNode.Predicate>(ParseOk(text).Where);
        Assert.Equal(expected, predicate.Field);
    }

    [Fact]
    public void BareTokens_AreValues_AndQuotedStringsUnescape()
    {
        var bare = Assert.IsType<RqlNode.Predicate>(ParseOk("space = ENG").Where);
        Assert.Equal("ENG", Assert.IsType<RqlValue.Text>(bare.Values[0]).Value);

        var quoted = Assert.IsType<RqlNode.Predicate>(
            ParseOk("""title = "a \"b\" \\ c\nd\te" """).Where);
        Assert.Equal("a \"b\" \\ c\nd\te", Assert.IsType<RqlValue.Text>(quoted.Values[0]).Value);
    }

    [Fact]
    public void Functions_Parse_WithAndWithoutArguments()
    {
        var currentUser = Assert.IsType<RqlNode.Predicate>(ParseOk("creator = currentUser()").Where);
        Assert.IsType<RqlValue.CurrentUser>(currentUser.Values[0]);

        var bareNow = Assert.IsType<RqlNode.Predicate>(ParseOk("updated > now()").Where);
        Assert.Null(Assert.IsType<RqlValue.Now>(bareNow.Values[0]).Offset);

        var offsetNow = Assert.IsType<RqlNode.Predicate>(ParseOk("""updated > now("-7d")""").Where);
        Assert.Equal("-7d", Assert.IsType<RqlValue.Now>(offsetNow.Values[0]).Offset);

        // An unsigned offset normalizes to an explicit '+', so the printed form reparses
        // to the identical AST.
        var unsigned = Assert.IsType<RqlNode.Predicate>(ParseOk("""updated > now("7d")""").Where);
        Assert.Equal("+7d", Assert.IsType<RqlValue.Now>(unsigned.Values[0]).Offset);
    }

    [Fact]
    public void OrderBy_IsEmptyWhenAbsent_AndDefaultsToAscendingWhenUndirected()
    {
        Assert.Empty(ParseOk("""label = "a" """).OrderBy);

        var single = Assert.Single(ParseOk("""label = "a" ORDER BY title""").OrderBy);
        Assert.Equal(RqlField.Title, single.Field);
        Assert.Equal(RqlSortDirection.Ascending, single.Direction);

        var multiple = ParseOk("""label = "a" ORDER BY created DESC, title ASC""").OrderBy;
        Assert.Equal(2, multiple.Count);
        Assert.Equal(RqlField.Created, multiple[0].Field);
        Assert.Equal(RqlSortDirection.Descending, multiple[0].Direction);
        Assert.Equal(RqlField.Title, multiple[1].Field);
        Assert.Equal(RqlSortDirection.Ascending, multiple[1].Direction);
    }

    [Fact]
    public void DateLiteral_WithoutTime_MeansTheWholeUtcDay()
    {
        var predicate = Assert.IsType<RqlNode.Predicate>(ParseOk("""created = "2026-01-31" """).Where);
        var date = Assert.IsType<RqlValue.Date>(predicate.Values[0]);

        Assert.Equal(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), date.StartUtc);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), date.EndExclusiveUtc);
    }

    [Fact]
    public void DateLiteral_WithTime_IsAnExactInstant_NormalizedToUtc()
    {
        var predicate = Assert.IsType<RqlNode.Predicate>(
            ParseOk("""created >= "2026-01-31T09:15:00+01:00" """).Where);
        var date = Assert.IsType<RqlValue.Date>(predicate.Values[0]);

        Assert.Null(date.EndExclusiveUtc);
        Assert.Equal(new DateTime(2026, 1, 31, 8, 15, 0, DateTimeKind.Utc), date.StartUtc);
        Assert.Equal("2026-01-31T08:15:00Z", date.CanonicalText);
    }

    [Theory]
    // Each case pins the offset the editor underlines, not just that an error happened.
    [InlineData("", 0, 0)]
    [InlineData("   ", 3, 0)]
    [InlineData("""space = """, 8, 0)]
    [InlineData("""space""", 5, 0)]
    [InlineData("""space == "A" """, 7, 1)]
    [InlineData("""(space = "A" """, 13, 0)]
    [InlineData("""space = "A" title = "B" """, 12, 5)]
    [InlineData("""label IN "a" """, 9, 3)]
    [InlineData("""label IS NOT "a" """, 13, 3)]
    [InlineData("""title = "unterminated""", 8, 13)]
    [InlineData("""title = "bad \q escape" """, 13, 2)]
    [InlineData("""title = #hash""", 8, 1)]
    [InlineData("""label = "a" ORDER title""", 18, 5)]
    public void MalformedInput_ReportsASyntaxErrorAtTheRightPosition(string text, int offset, int length)
    {
        var error = ParseError(text);

        Assert.Equal(RqlErrorCode.Syntax, error.Code);
        Assert.Equal(offset, error.Offset);
        Assert.Equal(length, error.Length);
    }

    [Fact]
    public void NestingDeeperThanTheCap_IsRejectedAsTooComplex()
    {
        var atCap = string.Concat(Enumerable.Repeat("(", RqlLimits.MaxDepth - 1))
            + """label = "a" """
            + string.Concat(Enumerable.Repeat(")", RqlLimits.MaxDepth - 1));
        Assert.True(Rql.Parse(atCap).IsValid);

        var overCap = string.Concat(Enumerable.Repeat("(", RqlLimits.MaxDepth))
            + """label = "a" """
            + string.Concat(Enumerable.Repeat(")", RqlLimits.MaxDepth));
        Assert.Equal(RqlErrorCode.TooComplex, ParseError(overCap).Code);
    }

    [Fact]
    public void MorePredicatesThanTheCap_IsRejectedAsTooComplex()
    {
        static string Conjunction(int count) =>
            string.Join(" AND ", Enumerable.Range(0, count).Select(i => $"""label = "l{i}" """));

        Assert.True(Rql.Parse(Conjunction(RqlLimits.MaxPredicates)).IsValid);
        Assert.Equal(RqlErrorCode.TooComplex, ParseError(Conjunction(RqlLimits.MaxPredicates + 1)).Code);
    }

    [Fact]
    public void LongerThanTheLengthCap_IsRejectedWithoutTokenizing()
    {
        var padded = """label = " """.TrimEnd() + new string('x', RqlLimits.MaxQueryLength) + "\"";
        var error = ParseError(padded);

        Assert.Equal(RqlErrorCode.TooComplex, error.Code);
        Assert.Contains("too long", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedInList_AndOversizedOrderBy_AreRejected()
    {
        var values = string.Join(", ", Enumerable.Range(0, RqlLimits.MaxValuesPerPredicate + 1).Select(i => $"\"l{i}\""));
        Assert.Equal(RqlErrorCode.TooComplex, ParseError($"label IN ({values})").Code);

        Assert.Equal(
            RqlErrorCode.TooComplex,
            ParseError("""label = "a" ORDER BY title, created, updated, title""").Code);
    }

    [Fact]
    public void KeywordsCannotBeBareValues_ButQuoteThemAndTheyAre()
    {
        Assert.Equal(RqlErrorCode.Syntax, ParseError("label = AND").Code);

        var predicate = Assert.IsType<RqlNode.Predicate>(ParseOk("""label = "AND" """).Where);
        Assert.Equal("AND", Assert.IsType<RqlValue.Text>(predicate.Values[0]).Value);
    }
}
