namespace RocketWiki.Importer.Conversion;

/// <summary>
/// Identifies the Confluence page whose storage-format body is being converted.
/// Supplies the space/page context needed to resolve attachment references that
/// omit an explicit source page (they belong to the page being converted) and to
/// resolve page links that omit an explicit space key (they default to this page's space).
/// </summary>
/// <param name="SpaceKey">The Confluence space key the page belongs to.</param>
/// <param name="PageTitle">The Confluence page title at export time.</param>
/// <param name="ContentId">The Confluence content id, if the export captured one.</param>
public sealed record ConfluencePageContext(string SpaceKey, string PageTitle, string? ContentId = null);
