using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Pipeline.Internal;

/// <summary>One page, positioned in the import order, with its final (disambiguated) slug and the Confluence id of the parent it should be created under (null for a root).</summary>
internal sealed record PlannedPage(ConfluenceExportPage Page, string Slug, string? ParentConfluencePageId);

internal sealed class ImportTreePlan
{
    /// <summary>Every importable page, in strict parent-before-child order (breadth-first from the roots) — safe to create sequentially without ever hitting a not-yet-created parent.</summary>
    public required IReadOnlyList<PlannedPage> OrderedPages { get; init; }

    /// <summary>Pages unreachable from any root — a parent-reference cycle, or a chain that eventually points back into itself. Confluence should never produce this, but silently dropping pages is worse than reporting an odd-sounding one.</summary>
    public required IReadOnlyList<ConfluenceExportPage> OrphanedPages { get; init; }
}

/// <summary>
/// Computes the page-creation order and slugs once, so both <see cref="ConfluenceSpaceImporter"/>
/// (writes for real) and <see cref="ConfluenceImportValidator"/> (dry run) walk the
/// <b>exact same tree</b> — a validator whose notion of the tree differs from the real
/// importer's would validate nothing.
/// </summary>
internal static class ImportTreePlanner
{
    public static ImportTreePlan Plan(ConfluenceExportSpace export)
    {
        var pagesById = export.Pages.ToDictionary(p => p.ConfluencePageId, StringComparer.Ordinal);

        var (orderedPages, orphaned) = ForestOrderer.OrderParentFirst(
            export.Pages,
            getId: p => p.ConfluencePageId,
            getParentId: p => p.ParentConfluencePageId is { } pid && pagesById.ContainsKey(pid) ? pid : null);

        // Slugs are unique per SPACE, not per parent. The slug is the page's address
        // (/spaces/{key}/{slug}) with the hierarchy deliberately absent from it, so that
        // moving a page never breaks a link — PageService says so and the unique index
        // IX_Pages_Space_Slug enforces it (widened from per-parent by migration
        // SpaceUniquePageSlug).
        //
        // This used to scope the set per parent, which the app stopped agreeing with and
        // nobody updated. The consequence was not subtle: any Confluence space with two
        // same-titled pages under different parents — "Overview", "Meeting Notes", "FAQ",
        // a year — planned the same slug twice, the second CreatePageAsync came back
        // ValidationError, and ConfluenceSpaceImporter then skipped that page's ENTIRE
        // subtree. That is the most common shape a real Confluence space has.
        //
        // Still computed in tree order, which is what makes the disambiguation suffix
        // deterministic rather than dependent on the order Confluence happened to list
        // pages in.
        var slugsInSpace = new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<PlannedPage>(orderedPages.Count);
        foreach (var page in orderedPages)
        {
            var parentId = page.ParentConfluencePageId is { } pid && pagesById.ContainsKey(pid) ? pid : null;
            planned.Add(new PlannedPage(page, Slugifier.Slugify(page.Title, slugsInSpace), parentId));
        }

        return new ImportTreePlan { OrderedPages = planned, OrphanedPages = orphaned };
    }
}
