using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Services;

/// <summary>
/// Maintains the page link index (<see cref="PageLink"/>) at every write that changes a
/// page's content. <b>Every assignment to <c>Page.CurrentContent</c> is followed by a call
/// here, in the same unit of work</b>: <c>PageService</c> (create, update, restore a
/// revision) and <c>BundleImportService</c> (page upsert). The Confluence importer writes
/// through <c>IPageService</c> and inherits it. A write path that forgets this call does
/// not fail — it leaves the graph quietly stale for that page — which is why the list is
/// short, named here, and pinned by a test per path rather than trusted.
///
/// <para><b>The whole set is replaced, never diffed against the old content.</b> The rows
/// a page should have are <see cref="PageLink.FromContent"/> of its current content, full
/// stop; the previous content is not consulted. What LOOKS like a diff below is EF
/// bookkeeping, not a semantic one: the change tracker cannot hold two instances with the
/// same key even when one is Deleted and the other Added, so a row that survives is
/// updated in place (its ordinal may have moved) rather than deleted and re-inserted.
/// The committed end state is identical to delete-all-then-insert-all.</para>
///
/// <para>Rows are added to the ambient change set and never saved here, so they commit in
/// the same transaction as the content, the revision and the audit row (design.md §7) —
/// and roll back with them. <c>ExecuteDelete</c> was rejected for exactly that reason: it
/// runs immediately, outside the pending SaveChanges, and a save that then failed would
/// leave a page with content and no index.</para>
///
/// <para>Tracker-aware, not just database-aware: a sync bundle applies every line in one
/// unsaved unit of work (see <c>BundleImportService.FindLocal</c>), so the same page can
/// be upserted several times before anything is flushed. Rows Added by an earlier upsert
/// are only in the tracker; rows Deleted by one may be wanted again by the next. Both are
/// reconciled from the tracker's entries, whatever their state, after the database's rows
/// for the page have been loaded into it.</para>
/// </summary>
internal static class PageLinkIndex
{
    /// <summary>
    /// Makes the tracked change set carry exactly <see cref="PageLink.FromContent"/> of
    /// <paramref name="page"/>'s current content for that page, and nothing else for it.
    /// One query — none for a page being inserted, which can have no rows yet.
    /// </summary>
    public static async Task ReplaceAsync(RocketWikiDbContext db, Page page, CancellationToken cancellationToken)
    {
        var desired = PageLink.FromContent(page.Id, page.CurrentContent).ToDictionary(l => l.TargetPageId);

        if (db.Entry(page).State != EntityState.Added)
        {
            // Load (not query-and-copy): the rows land in the tracker, identity-resolved
            // against any already there, so the reconciliation below sees one instance per
            // key whatever mix of database and unit-of-work state produced it.
            await db.PageLinks.Where(l => l.SourcePageId == page.Id).LoadAsync(cancellationToken);
        }

        foreach (var entry in db.ChangeTracker.Entries<PageLink>().Where(e => e.Entity.SourcePageId == page.Id).ToList())
        {
            if (desired.Remove(entry.Entity.TargetPageId, out var wanted))
            {
                entry.Entity.Ordinal = wanted.Ordinal;
                if (entry.State == EntityState.Deleted)
                {
                    // Removed by an earlier write in this unit of work and wanted again by
                    // this one: the row never left the database, so it is an update.
                    entry.State = EntityState.Modified;
                }
            }
            else if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached; // never reached the database; forget it
            }
            else if (entry.State != EntityState.Deleted)
            {
                entry.State = EntityState.Deleted;
            }
        }

        db.PageLinks.AddRange(desired.Values);
    }
}
