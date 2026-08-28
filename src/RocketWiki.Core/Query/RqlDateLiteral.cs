using System.Globalization;

namespace RocketWiki.Core.Query;

/// <summary>
/// Parsing for the two date forms RQL accepts (design.md §22): an ISO-8601 date literal
/// and a <c>now()</c> offset. Pure, culture-invariant, and shared by the validator (which
/// reports a bad one with a position) and the printer (which re-emits the canonical form),
/// so the two can never disagree about what a date means.
/// </summary>
public static class RqlDateLiteral
{
    /// <summary>Instant formats, ordered most-specific first. <c>K</c> accepts a trailing
    /// <c>Z</c>, an explicit offset, or neither — with <c>AssumeUniversal</c>, "neither"
    /// means UTC, which is the only defensible reading for a wiki whose every stored
    /// timestamp is UTC.</summary>
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mmK",
    ];

    private const string DateOnlyFormat = "yyyy-MM-dd";

    private const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>Longest offset magnitude accepted, in whole units — enough for any plausible
    /// "in the last N" filter and far short of anything that could overflow a DateTime.</summary>
    private const int MaxOffsetAmount = 100_000;

    /// <summary>
    /// Parses an ISO-8601 date literal.
    ///
    /// <para>A <b>date without a time</b> denotes the whole UTC day: <c>endExclusiveUtc</c>
    /// comes back non-null and the compiler turns <c>created = "2026-01-31"</c> into a range
    /// test. Anything else would make day-precision equality match nothing except a page
    /// created at exactly midnight, which is never what an author means. An instant
    /// (<c>"2026-01-31T09:15:00Z"</c>) is exact and comes back with a null end.</para>
    /// </summary>
    public static bool TryParse(
        string text, out DateTime startUtc, out DateTime? endExclusiveUtc, out string canonicalText)
    {
        if (DateTime.TryParseExact(text, DateOnlyFormat, CultureInfo.InvariantCulture, Styles, out var day))
        {
            startUtc = day;
            endExclusiveUtc = day.AddDays(1);
            canonicalText = day.ToString(DateOnlyFormat, CultureInfo.InvariantCulture);
            return true;
        }

        if (DateTime.TryParseExact(text, InstantFormats, CultureInfo.InvariantCulture, Styles, out var instant))
        {
            startUtc = instant;
            endExclusiveUtc = null;
            canonicalText = FormatInstant(instant);
            return true;
        }

        startUtc = default;
        endExclusiveUtc = null;
        canonicalText = string.Empty;
        return false;
    }

    /// <summary>The canonical spelling of an instant: always UTC, always with an explicit
    /// <c>Z</c>, sub-second precision only when it is non-zero. Reparsing this string yields
    /// the same instant, which is what keeps <c>Print(Parse(x))</c> a fixed point.</summary>
    public static string FormatInstant(DateTime utc) =>
        utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a <c>now()</c> offset such as <c>-7d</c> or <c>+1h</c>. Units are
    /// <c>w</c> (weeks), <c>d</c> (days), <c>h</c> (hours) and <c>m</c> (minutes); a missing
    /// sign means <c>+</c>, and the canonical form always carries one so the printer's output
    /// reparses to the identical AST.
    /// </summary>
    public static bool TryParseOffset(string text, out TimeSpan offset, out string canonicalText)
    {
        offset = default;
        canonicalText = string.Empty;

        if (text.Length < 2)
        {
            return false;
        }

        var negative = text[0] == '-';
        var digitsStart = text[0] is '+' or '-' ? 1 : 0;
        var unit = char.ToLowerInvariant(text[^1]);
        var digits = text[digitsStart..^1];

        if (digits.Length == 0
            || !digits.All(char.IsAsciiDigit)
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount > MaxOffsetAmount)
        {
            return false;
        }

        var unitSpan = unit switch
        {
            'w' => TimeSpan.FromDays(7),
            'd' => TimeSpan.FromDays(1),
            'h' => TimeSpan.FromHours(1),
            'm' => TimeSpan.FromMinutes(1),
            _ => TimeSpan.Zero,
        };

        if (unitSpan == TimeSpan.Zero)
        {
            return false;
        }

        offset = negative ? -(unitSpan * amount) : unitSpan * amount;
        canonicalText = string.Format(
            CultureInfo.InvariantCulture, "{0}{1}{2}", negative ? '-' : '+', amount, unit);
        return true;
    }
}
