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
}
