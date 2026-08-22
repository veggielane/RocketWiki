namespace RocketWiki.Importer.Conversion;

/// <summary>
/// The output of converting one Confluence page's storage-format body: the resulting
/// RocketWiki Markdown plus the report of everything that was lossy along the way.
/// </summary>
public sealed record ConversionResult(string Markdown, ConversionReport Report);
