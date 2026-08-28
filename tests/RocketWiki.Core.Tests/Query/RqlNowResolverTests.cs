using RocketWiki.Core.Query;
using Xunit;

namespace RocketWiki.Core.Tests.Query;

/// <summary>
/// design.md §22: <c>now()</c> is resolved <b>once per query execution</b>. Two predicates in
/// one query must not be able to disagree about what "now" was — a window built from two
/// clock reads is a window nobody asked for, and across a boundary it can be empty.
/// </summary>
public class RqlNowResolverTests
{
    private static readonly DateTime Instant = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static List<RqlValue.Date> ResolvedDates(string query)
    {
        var parsed = Rql.Parse(query);
        Assert.True(parsed.IsValid, string.Join(" | ", parsed.Errors.Select(e => e.Message)));

        var dates = new List<RqlValue.Date>();
        Walk(RqlNowResolver.Resolve(parsed.Query!, Instant).Where, dates);
        return dates;
    }

    private static void Walk(RqlNode node, List<RqlValue.Date> dates)
    {
        switch (node)
        {
            case RqlNode.And and:
                foreach (var child in and.Children)
                {
                    Walk(child, dates);
                }

                break;
            case RqlNode.Or or:
                foreach (var child in or.Children)
                {
                    Walk(child, dates);
                }

                break;
            case RqlNode.Not not:
                Walk(not.Child, dates);
                break;
            case RqlNode.Predicate predicate:
                dates.AddRange(predicate.Values.OfType<RqlValue.Date>());
                break;
        }
    }

    [Fact]
    public void EveryNowInOneQueryResolvesFromTheSameInstant()
    {
        var dates = ResolvedDates("""updated >= now("-1d") AND updated < now() AND created > now("-1w")""");

        Assert.Equal(
            [Instant.AddDays(-1), Instant, Instant.AddDays(-7)],
            dates.Select(d => d.StartUtc).ToArray());

        // A resolved now() is an instant, never a day range - "in the last seven days to the
        // minute", not "since the start of that day".
        Assert.All(dates, d => Assert.Null(d.EndExclusiveUtc));
    }

    [Theory]
    [InlineData("""now("-7d")""", -7 * 24 * 60)]
    [InlineData("""now("+1h")""", 60)]
    [InlineData("""now("2w")""", 2 * 7 * 24 * 60)]
    [InlineData("""now("-30m")""", -30)]
    [InlineData("now()", 0)]
    public void OffsetUnitsAreApplied(string call, int expectedMinutes)
    {
        var date = Assert.Single(ResolvedDates($"updated > {call}"));
        Assert.Equal(Instant.AddMinutes(expectedMinutes), date.StartUtc);
    }

    [Fact]
    public void LiteralDatesAreLeftAlone()
    {
        var date = Assert.Single(ResolvedDates("""created = "2026-01-31" """));

        Assert.Equal(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), date.StartUtc);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), date.EndExclusiveUtc);
    }
}
