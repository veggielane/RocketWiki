namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.15: one configured selector category — <c>FRUIT</c> with values
/// <c>APPLE</c> and <c>BANANA</c>, say. Categories come from the instance's
/// configuration (<c>ProtectiveMarking:SelectorCategories</c>), never from the database
/// and never from the attribute registry: a selector is a deployment-time fact about what
/// this instance's markings may say, exactly like the instance id.
///
/// <para>A category used to carry a <c>ClaimName</c> as well — the Keycloak attribute
/// whose <c>yes</c> made a principal eligible for the category before the space's grant
/// was consulted. It is gone, with the eligibility gate it fed (see
/// <see cref="SelectorGate"/>): this deployment carries no per-category attributes in
/// Keycloak, so eligibility is not a fact anyone can state about a principal here, and
/// a category gates through the space's access grants alone.</para>
/// </summary>
/// <param name="Name">The category name, canonical (upper-cased, <c>[A-Z0-9_-]</c>). It is
/// the token that appears in denial reasons (<c>selector:not_granted:FRUIT</c>), so it is
/// bounded configured vocabulary, never free text.</param>
/// <param name="Description">Free text for pickers and admin screens. No rules; never
/// enforced against anything.</param>
/// <param name="Values">The values a marking or a grant may carry in this category,
/// canonical, in configured order. At least one; a value outside this list is unknown to
/// the instance and matches nobody.</param>
public sealed record SelectorCategory(
    string Name,
    string? Description,
    IReadOnlyList<string> Values);
