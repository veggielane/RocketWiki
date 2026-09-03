namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.15: one configured selector category — <c>FRUIT</c> with values
/// <c>APPLE</c> and <c>BANANA</c>, say. Categories come from the instance's
/// configuration (<c>ProtectiveMarking:SelectorCategories</c>), never from the database
/// and never from the attribute registry: a selector is a deployment-time fact about what
/// this instance's markings may say, exactly like the instance id.
/// </summary>
/// <param name="Name">The category name, canonical (upper-cased, <c>[A-Z0-9_-]</c>). It is
/// the token that appears in denial reasons (<c>selector:not_eligible:FRUIT</c>), so it is
/// bounded configured vocabulary, never free text.</param>
/// <param name="Description">Free text for pickers and admin screens. No rules; never
/// enforced against anything.</param>
/// <param name="ClaimName">
/// The principal attribute that gates <b>eligibility</b> for this category
/// (<c>fruit</c>): a principal is eligible when that attribute holds the value
/// <c>yes</c> (see <see cref="SelectorGate.EligibilityClaimValue"/>). <b>Null means
/// everyone is eligible</b> and the category is gated by the per-space grant alone.
/// Deliberately not a registry key: it is mapped from the token by the API's principal
/// builder because the catalog names it, and it never becomes a rule-builder attribute.
/// </param>
/// <param name="Values">The values a marking or a grant may carry in this category,
/// canonical, in configured order. At least one; a value outside this list is unknown to
/// the instance and matches nobody.</param>
public sealed record SelectorCategory(
    string Name,
    string? Description,
    string? ClaimName,
    IReadOnlyList<string> Values)
{
    /// <summary>True when eligibility is gated by a claim; false when everyone is eligible.</summary>
    public bool RequiresClaim => ClaimName is not null;
}
