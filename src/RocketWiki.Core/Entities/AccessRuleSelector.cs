namespace RocketWiki.Core.Entities;

/// <summary>
/// One selector value an access grant confers (design.md §21.15): every principal the
/// grant's expression matches is granted <c>Value</c> in <c>Category</c> in that space.
/// A grant may carry any number of values, including several in one category — hence
/// the composite PK <c>(AccessRuleId, Category, Value)</c>, unlike
/// <see cref="PageMarkingSelector"/>'s <c>(PageId, Category)</c>. The calculator unions
/// these rows over every matching access grant; a page's selector must be in that union
/// (the G gate) for the page to be readable.
///
/// <para>A child table rather than a delimited column for the reason the marking's own
/// selectors are one: this is enforcement-critical data and must be queryable as data
/// ("which grants confer APPLE"). Values are canonical (upper-case) on write and
/// validated against the configured catalog by <c>AccessRuleService</c>; a row whose
/// category has since left the configuration confers nothing useful, because E fails
/// before G is consulted.</para>
/// </summary>
public class AccessRuleSelector
{
    public Guid AccessRuleId { get; set; }

    public AccessRule? AccessRule { get; set; }

    /// <summary>Canonical category name.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Canonical value within the category.</summary>
    public string Value { get; set; } = string.Empty;
}
