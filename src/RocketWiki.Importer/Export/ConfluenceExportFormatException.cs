namespace RocketWiki.Importer.Export;

/// <summary>
/// Thrown when an archive doesn't look like a Confluence XML space export (no
/// entities.xml, no Space object, missing a property this reader relies on). Confluence's
/// export format is not something this converter has been run against live — see
/// <see cref="ConfluenceXmlExportReader"/>'s remarks — so failing loudly here, rather than
/// silently producing an empty space, is what makes that gap safe to ship with.
/// </summary>
public sealed class ConfluenceExportFormatException : Exception
{
    public ConfluenceExportFormatException(string message)
        : base(message)
    {
    }
}
