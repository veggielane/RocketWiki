using RocketWiki.Core.Content;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md: PageLink — the page link index, one row per distinct
/// <c>page://{guid}</c> a page's live Markdown names. Derived from
/// <see cref="Page.CurrentContent"/> by <see cref="PageLinkScanner"/> and nothing else:
/// never synced (a replica rebuilds it from the content the bundle carries), never
/// edited by a user, and rebuilt in full on every write that changes a page's content —
/// the whole set for the source page is replaced, never diffed, so the index can only
/// ever say what the content says.
///
/// <para><b>The primary key is <c>(SourcePageId, TargetPageId)</c></b>, so a page links
/// to another at most once however many anchors repeat it; <see cref="Ordinal"/> is the
/// first-occurrence position among the page's distinct targets (0-based, the scanner's
/// order), so a client holding the content can pair edges with anchors positionally.
/// The <c>TargetPageId</c> index is the backlink direction: "which pages link here".</para>
///
/// <para><b>There is deliberately no foreign key on <see cref="TargetPageId"/>.</b> A
/// link may name a page that does not exist — an author pasted an id that never
/// resolved, or the target was hard-deleted later — and a constraint there would either
/// refuse a legitimate save (the author's content is valid Markdown whatever the id
/// names) or force the indexer to silently drop the row, losing the fact that the page
/// says it links somewhere. Dangling targets are stored and filtered at READ time
/// instead, where the graph service joins every endpoint back to a live page anyway. A
/// trashed target is the everyday case: its row stays so that restoring the target
/// restores the backlinks, exactly as a trashed page keeps its marking and labels.</para>
///
/// <para>The source side does carry a foreign key, and it CASCADES — the one FK in the
/// schema that does (data-model.md's conventions state the exception). This row is not
/// content and not a record of anything a person did; it is a derived index of the
/// source page, has no lifecycle of its own, and a hard delete of the page (which the
/// product never performs — pages are trashed, and the trash keeps the row) would leave
/// it meaningless. NO ACTION exists to stop a delete from silently destroying something
/// worth keeping, and there is nothing here worth keeping apart from its page.</para>
///
/// <para>No navigation properties, deliberately. A <c>Page</c> navigation on an index row
/// would be a Page-shaped route around the object-level authorization every page read
/// goes through (design.md §6.7); the graph service resolves both endpoints through the
/// batched permission path and never through this row.</para>
/// </summary>
public class PageLink
{
    public Guid SourcePageId { get; set; }
    public Guid TargetPageId { get; set; }

    /// <summary>First-occurrence position among the source page's distinct targets, 0-based.</summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// The complete index for one page's content — every row the page should have and
    /// nothing else, in scanner order with dense ordinals. THE definition of what the
    /// index holds: the incremental maintainer (<c>PageLinkIndex</c> in RocketWiki.Data)
    /// writes exactly this set, and the <c>AddPageLinks</c> migration's backfill is pinned
    /// by test to produce the same rows for the same content.
    /// </summary>
    public static IReadOnlyList<PageLink> FromContent(Guid sourcePageId, string? content)
    {
        var targets = PageLinkScanner.Extract(content);
        var links = new PageLink[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            links[i] = new PageLink { SourcePageId = sourcePageId, TargetPageId = targets[i], Ordinal = i };
        }

        return links;
    }
}
