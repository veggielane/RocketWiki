namespace RocketWiki.Core.Entities;

/// <summary>
/// One additional selector on a page's marking (design.md §21.15): the page carries
/// <c>Value</c> in <c>Category</c>, e.g. <c>APPLE</c> in <c>FRUIT</c>. A child table
/// rather than a delimited column, for the same reason <see cref="PageMarkingCountry"/>
/// is one: this is enforcement-critical data and must be queryable as data — "which
/// pages carry APPLE" is a report someone will want, and a <c>LIKE</c> over a packed
/// string is the kind of near-miss that quietly answers the wrong question about an
/// access control.
///
/// <para><b>Composite PK <c>(PageId, Category)</c></b> — the category, not the value.
/// That is what makes "at most one value per category on a page" a database fact rather
/// than application discipline: a second value for the same category is a primary-key
/// violation. Contrast <see cref="AccessRuleSelector"/>, keyed on the value too, because a
/// grant may confer several values of one category. Canonical form (upper-case, trimmed)
/// is applied on write by <c>ProtectiveMarking</c>; ordinal order for display and
/// serialization is derived, never stored.</para>
/// </summary>
public class PageMarkingSelector
{
    public Guid PageId { get; set; }

    public PageMarking? Marking { get; set; }

    /// <summary>Canonical category name — configured vocabulary, see <c>SelectorCatalog</c>.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Canonical value within the category.</summary>
    public string Value { get; set; } = string.Empty;
}
