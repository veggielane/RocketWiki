namespace RocketWiki.Importer.Conversion;

/// <summary>
/// Thrown when a page's storage-format body cannot be parsed at all — e.g. it is not
/// well-formed XML, or it uses a named HTML entity this converter does not recognize.
/// This is distinct from a lossy conversion: a lossy page still produces Markdown plus a
/// report entry, but a page that fails to parse produces nothing, and that failure must be
/// visible rather than silently skipped or rendered as an empty page.
/// </summary>
public sealed class ConfluenceConversionException : Exception
{
    public ConfluenceConversionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
