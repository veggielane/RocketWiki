namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.15: one <b>additional selector</b> — a (category, value) pair such as
/// <c>FRUIT</c>/<c>APPLE</c>. On a page's marking a category appears at most once
/// (<see cref="ProtectiveMarking.Create"/> refuses two values for one category, and the
/// <c>PageMarkingSelectors</c> primary key makes it a database fact); on an access grant
/// any number of values may be conferred, one row each.
///
/// <para><b>Canonical by construction.</b> Both tokens are trimmed and upper-cased in the
/// constructor, so an instance in a non-canonical state cannot exist and every comparison
/// — a marking's selector against a grant's, a stored row against the configured catalog —
/// is a plain ordinal one. The same normalize-on-write discipline the caveat countries
/// follow (§21.4), and the same documented departure from §6.3's ordinal-no-folding rule,
/// confined to marking comparisons: the two sides of a selector comparison are deployment
/// configuration and operator input, and a casing mismatch between them would deny every
/// legitimate reader while looking correct. <c>attr</c> conditions stay ordinal.</para>
///
/// <para>Value-equal (a record), so a set of them de-duplicates and a marking's selector
/// list compares element-wise. Sorting is ordinal by category then value; see
/// <see cref="ProtectiveMarking.Selectors"/> for why the stored order is ordinal while the
/// <i>displayed</i> order is the catalog's.</para>
/// </summary>
public sealed record SelectorValue
{
    public SelectorValue(string category, string value)
    {
        Category = Canonicalize(category);
        Value = Canonicalize(value);
    }

    /// <summary>The category name, canonical (trimmed, upper-cased) — e.g. <c>FRUIT</c>.</summary>
    public string Category { get; }

    /// <summary>The value within the category, canonical — e.g. <c>APPLE</c>.</summary>
    public string Value { get; }

    /// <summary>
    /// The canonical form of one selector token: trimmed, upper-cased with the invariant
    /// culture. Shared by the catalog (so configured names and values are canonical), the
    /// marking, the grant rows and the sync importer, so all of them agree byte-for-byte.
    /// </summary>
    public static string Canonicalize(string token) => (token ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Named factory, for call sites where "this canonicalizes" is worth stating.</summary>
    public static SelectorValue Canonical(string category, string value) => new(category, value);

    /// <summary>
    /// The ordering used wherever selectors are stored or compared as a sequence:
    /// ordinal by category, then ordinal by value. Ordinal rather than culture-aware for
    /// the same reason the country set is (§21.4): the audit details, the sync payload and
    /// the stored rows must serialize identically on every machine.
    /// </summary>
    public static IComparer<SelectorValue> CanonicalOrder { get; } = new CanonicalComparer();

    public override string ToString() => $"{Category}/{Value}";

    private sealed class CanonicalComparer : IComparer<SelectorValue>
    {
        public int Compare(SelectorValue? x, SelectorValue? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var byCategory = string.CompareOrdinal(x.Category, y.Category);
            return byCategory != 0 ? byCategory : string.CompareOrdinal(x.Value, y.Value);
        }
    }
}
