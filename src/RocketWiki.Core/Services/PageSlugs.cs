namespace RocketWiki.Core.Services;

/// <summary>
/// Rules about a page's slug, which is now an address rather than decoration: a page
/// is reachable at <c>/spaces/{spaceKey}/{slug}</c>.
///
/// <para>Slugs are unique per SPACE, not per parent. That is what lets the hierarchy
/// stay out of the URL, which in turn is what lets a page be moved without breaking
/// every link to it — the whole point of addressing this way rather than by path.</para>
/// </summary>
public static class PageSlugs
{
    /// <summary>
    /// The one slug a page may not take.
    ///
    /// <para>Every system page for a space lives under a <c>-</c> segment —
    /// <c>/spaces/{key}/-/admin</c>, <c>/-/grants</c>, <c>/-/trash</c> — so the only
    /// thing a page slug can collide with is that segment itself. This used to be a
    /// list of every such route's word (admin, grants, trash, import-report), which
    /// had to grow in lockstep with the router: adding a space route without adding
    /// its word here would silently shadow every page already using it, with no error
    /// anywhere. One reserved token cannot fall out of step.</para>
    ///
    /// <para>Only the exact string, not anything containing it: "admin-guide" and
    /// "well-known" are perfectly good addresses, and "--" is a different segment from
    /// "-". <c>SlugifyTitle</c> can never produce a bare "-" (it trims leading and
    /// trailing hyphens, so such a title slugifies to empty), so this only ever fires
    /// for a slug someone typed deliberately.</para>
    /// </summary>
    public const string SystemSegment = "-";

    public static bool IsReserved(string slug) =>
        string.Equals(slug.Trim(), SystemSegment, StringComparison.Ordinal);

    /// <summary>
    /// THE canonical form of a slug: trimmed and lower-cased invariantly.
    ///
    /// <para>URLs are case-insensitive here — <c>/spaces/ENG/My-Page</c> and
    /// <c>/spaces/eng/my-page</c> address the same page — and this is half of how. The
    /// other half is <see cref="SpaceKeys.Canonical"/>. Case-folding rather than
    /// rejecting mixed case is what people expect of an address; nobody wants a 404 for
    /// capitalising a link they were sent.</para>
    ///
    /// <para><b>Both halves are required, and neither works alone.</b> A canonical stored
    /// form without normalised lookups means a typed <c>My-Page</c> misses; normalised
    /// lookups without a canonical stored form means <c>my-page</c> and <c>My-Page</c> can
    /// both exist and the lookup becomes ambiguous — one URL, two pages, no defined
    /// winner. Storing canonically is also what makes the case-sensitive BIN2 unique index
    /// (data-model.md) enforce case-INsensitive uniqueness: once every stored slug is
    /// lower-case, "already taken" and "taken in a different case" are the same question.</para>
    ///
    /// <para>Invariant culture, not the current one: a slug is machine-facing text, and
    /// Turkish's dotless i would otherwise make the same title fold two ways depending on
    /// the server's locale.</para>
    /// </summary>
    public static string Canonical(string slug) => slug.Trim().ToLowerInvariant();
}

/// <summary>
/// Rules about a space's key — the first segment of <c>/spaces/{key}/{slug}</c>, and the
/// name a sync bundle carries a space under (design.md §12).
/// </summary>
public static class SpaceKeys
{
    /// <summary>
    /// THE canonical form of a space key: trimmed and UPPER-cased invariantly, the
    /// <c>ENG</c> shape data-model.md's own example uses and every space in the product
    /// already reads as. See <see cref="PageSlugs.Canonical"/> for why a canonical stored
    /// form and normalised lookups are both needed and why neither works alone.
    ///
    /// <para>Upper rather than lower is the only difference from a slug, and it is
    /// presentational: a key is a short deliberate identifier that appears in banners and
    /// audit rows, where <c>ENG</c> reads as an identifier and <c>eng</c> reads as a typo.
    /// The direction also matches the data already in the estate, so the migration folds
    /// keys toward the form the docs show rather than away from it.</para>
    ///
    /// <para>Safe to canonicalize on write precisely because a key is immutable after
    /// creation — no mutation changes it (data-model.md's open question recommends
    /// exactly that, and no rename path touches it) — so there is no later edit for this
    /// to fight with.</para>
    /// </summary>
    public static string Canonical(string key) => key.Trim().ToUpperInvariant();

    /// <summary>Canonical form of an OPTIONAL key — the shape every "filter to one space"
    /// parameter takes (search, analytics, label listings). Null stays null: absent means
    /// "every space", not "the space whose key is empty". A distinct name rather than an
    /// overload because <c>string</c> and <c>string?</c> are the same signature to the
    /// runtime, and a nullable-oblivious overload pair is how a null slips through as
    /// "".</summary>
    public static string? CanonicalOrNull(string? key) => key is null ? null : Canonical(key);
}
