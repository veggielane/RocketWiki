namespace RocketWiki.Api.Markings;

/// <summary>
/// The <c>ProtectiveMarking</c> configuration section (design.md §21.15;
/// docs/CONFIGURATION.md "Protective markings"): the instance's additional-selector
/// categories. Bound by <see cref="ProtectiveMarkingConfiguration"/> and validated into a
/// Core <c>SelectorCatalog</c> at startup — every rule (canonical upper-case tokens from
/// <c>[A-Z0-9_-]</c>, ≤32 characters, unique, at least one value) lives in
/// <c>SelectorCatalog.TryCreate</c>, so the options class carries the shape and nothing
/// else.
///
/// <para><b>Unset is a supported state</b>: no categories means no selector pickers, grants
/// carry no values, and a page that somehow carries a selector (a bundle from an instance
/// that configured one) is visible to nobody — the fail-closed reading, never a bypass.
/// Environment form: <c>ProtectiveMarking__SelectorCategories__0__Name=FRUIT</c>,
/// <c>ProtectiveMarking__SelectorCategories__0__Values__1=BANANA</c>.</para>
///
/// <para>A category used to take a <c>ClaimName</c> — the Keycloak attribute whose
/// <c>yes</c> made a principal eligible for it. That setting is gone with the gate that
/// read it (this deployment carries no per-category attributes in Keycloak); a value
/// still present in an old environment is ignored by the binder, and a category is
/// gated by the space's access grants alone.</para>
/// </summary>
public sealed class ProtectiveMarkingOptions
{
    public const string SectionName = "ProtectiveMarking";

    public List<SelectorCategoryOptions> SelectorCategories { get; set; } = [];
}

/// <summary>One configured category. See <c>SelectorCategory</c> for what each member means.</summary>
public sealed class SelectorCategoryOptions
{
    /// <summary>The category name; canonicalized to upper-case. Appears in denial reasons.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free text for pickers and admin screens.</summary>
    public string? Description { get; set; }

    /// <summary>The values a marking or a grant may carry in this category.</summary>
    public List<string> Values { get; set; } = [];
}
