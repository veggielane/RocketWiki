namespace RocketWiki.Importer.Conversion.Internal;

/// <summary>
/// Mutable state threaded through one page's conversion: the page/space context for link
/// resolution, the resolver itself, the report being built, and the current heading path
/// (used to locate issues for a content owner reading the report).
/// </summary>
internal sealed class RenderState
{
    public required ConfluencePageContext PageContext { get; init; }

    public required IPageIdResolver Resolver { get; init; }

    public required ConversionReport Report { get; init; }

    /// <summary>Titles of the headings currently in scope, index 0 = the current h1 ancestor.</summary>
    public List<string> HeadingPath { get; } = [];

    public string? CurrentLocation => HeadingPath.Count == 0 ? null : string.Join(" > ", HeadingPath);

    /// <summary>Updates the heading path when a heading of the given level (1-6) is rendered.</summary>
    public void EnterHeading(int level, string title)
    {
        while (HeadingPath.Count > level - 1)
        {
            HeadingPath.RemoveAt(HeadingPath.Count - 1);
        }

        HeadingPath.Add(title);
    }
}
