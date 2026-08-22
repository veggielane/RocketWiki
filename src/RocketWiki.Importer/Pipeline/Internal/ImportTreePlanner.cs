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

        // Slugs must be unique per parent (data-model.md scopes the constraint to
        // siblings, not the whole space), computed in tree order so a title seen twice
        // under the same parent disambiguates deterministically regardless of which
        // sibling Confluence happened to list first.
        var slugScopesByParent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var planned = new List<PlannedPage>(orderedPages.Count);
        foreach (var page in orderedPages)
        {
            var parentId = page.ParentConfluencePageId is { } pid && pagesById.ContainsKey(pid) ? pid : null;
            var siblingScopeKey = parentId ?? "$root";
            var siblingSlugs = slugScopesByParent.TryGetValue(siblingScopeKey, out var scope)
                ? scope
                : slugScopesByParent[siblingScopeKey] = new HashSet<string>(StringComparer.Ordinal);

            planned.Add(new PlannedPage(page, Slugifier.Slugify(page.Title, siblingSlugs), parentId));
        }

        return new ImportTreePlan { OrderedPages = planned, OrphanedPages = orphaned };
    }
}
