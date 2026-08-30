using System.Xml;
using System.Xml.Linq;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Conversion;

/// <summary>
/// Converts a Confluence page's storage-format XHTML body to RocketWiki Markdown
/// (design.md §13). Handles the ordinary content model (headings, formatting, lists,
/// tables, links, images) plus the common structured macros, degrading anything outside
/// the v1 Markdown feature set (design.md §4) gracefully and recording every such case in
/// the returned <see cref="ConversionReport"/> rather than dropping it silently.
/// </summary>
/// <remarks>
/// This class converts one page's body at a time and has no knowledge of the wider import
/// pipeline (page creation order, attachment upload, author mapping) — those are the
/// pipeline's job. It resolves internal links and attachment references purely through the
/// injected <see cref="IPageIdResolver"/>, so it can run — and be tested — with no database
/// and no Confluence server.
/// </remarks>
public sealed partial class ConfluenceStorageConverter
{
    private readonly IPageIdResolver _resolver;

    public ConfluenceStorageConverter(IPageIdResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Converts one page's storage-format body to Markdown.
    /// </summary>
    /// <param name="storageXhtml">
    /// The raw <c>&lt;storage&gt;</c> representation body value from the Confluence export
    /// (a bare XHTML fragment — it has no single root element and declares no namespaces of
    /// its own; those are supplied here).
    /// </param>
    /// <param name="pageContext">The Confluence page this body belongs to.</param>
    /// <exception cref="ConfluenceConversionException">
    /// The body is not well-formed XML, or uses a named HTML entity this converter does not
    /// recognize (see <see cref="Internal.HtmlEntities"/>). Confluence's storage format is
    /// always well-formed, so this indicates something worth investigating rather than a
    /// page to silently skip.
    /// </exception>
    public ConversionResult Convert(string storageXhtml, ConfluencePageContext pageContext)
    {
        ArgumentNullException.ThrowIfNull(pageContext);
        storageXhtml ??= string.Empty;

        var report = new ConversionReport();
        var state = new RenderState
        {
            PageContext = pageContext,
            Resolver = _resolver,
            Report = report,
        };

        var root = ParseFragment(storageXhtml, pageContext);
        var blocks = RenderBlocks(root.Nodes(), state);
        var markdown = string.Join("\n\n", blocks.Where(b => !string.IsNullOrEmpty(b)));

        return new ConversionResult(markdown.Length == 0 ? string.Empty : markdown.TrimEnd() + "\n", report);
    }

    private static XElement ParseFragment(string storageXhtml, ConfluencePageContext pageContext)
    {
        var decoded = HtmlEntities.Decode(storageXhtml);
        var wrapped =
            $"<rw-import-root xmlns:ac=\"{ConfluenceNamespaces.AcUri}\" xmlns:ri=\"{ConfluenceNamespaces.RiUri}\">{decoded}</rw-import-root>";

        try
        {
            // XXE, stated rather than inherited — the same reasoning as EntityGraph.Parse.
            // The page body being parsed here is content from another organisation's
            // Confluence, and `XElement.Parse(string)` is safe today only because of a
            // framework default. Prohibit + a null resolver say so, and survive both a
            // runtime change and an edit that reaches back for the convenience overload.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                // The fragment is already in memory as a string, so its length IS the
                // bound; the reader is told so rather than left unbounded.
                ConformanceLevel = ConformanceLevel.Document,
            };

            using var reader = XmlReader.Create(new StringReader(wrapped), settings);
            return XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new ConfluenceConversionException(
                $"Page '{pageContext.PageTitle}' (space {pageContext.SpaceKey}) contains storage-format " +
                $"XHTML that could not be parsed as XML: {ex.Message}. This is usually an HTML entity " +
                "this converter does not recognize (see Internal.HtmlEntities) rather than malformed " +
                "Confluence storage — Confluence itself never stores non-well-formed XML.",
                ex);
        }
    }
}
