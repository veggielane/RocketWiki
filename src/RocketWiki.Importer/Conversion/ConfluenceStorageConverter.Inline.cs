using System.Text;
using System.Xml.Linq;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Conversion;

public sealed partial class ConfluenceStorageConverter
{
    private static string RenderInline(IEnumerable<XNode> nodes, RenderState state)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    // Collapse, don't trim: a text node ending in whitespace right before an
                    // inline element (e.g. "state: " before a macro) still needs that space.
                    sb.Append(MarkdownText.Escape(MarkdownText.CollapseWhitespace(text.Value)));
                    break;
                case XElement element:
                    sb.Append(RenderInlineElement(element, state));
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a complete, self-contained span of inline content (a whole paragraph, a
    /// heading, link text, a task item body) and trims the result. Trimming is deliberately
    /// deferred to here rather than done per text node — see <see cref="MarkdownText.CollapseWhitespace"/>.
    /// </summary>
    private static string RenderInlineTrimmed(IEnumerable<XNode> nodes, RenderState state) =>
        RenderInline(nodes, state).Trim();

    private static string RenderInlineElement(XElement element, RenderState state)
    {
        if (element.Name.Namespace == ConfluenceNamespaces.Ac)
        {
            return RenderAcInlineElement(element, state);
        }

        switch (element.Name.LocalName.ToLowerInvariant())
        {
            case "strong": case "b":
                return Wrap(RenderInline(element.Nodes(), state), "**");
            case "em": case "i":
                return Wrap(RenderInline(element.Nodes(), state), "*");
            case "s": case "strike": case "del":
                return Wrap(RenderInline(element.Nodes(), state), "~~");
            case "u":
                state.Report.Add(new ConversionIssue(
                    IssueSeverity.Lossy,
                    IssueCategory.LossyTransform,
                    "Underline formatting has no Markdown/GFM representation (design.md §4) and was removed; the underlying text was kept.",
                    state.CurrentLocation));
                return RenderInline(element.Nodes(), state);
            case "code":
                return WrapCode(MarkdownText.NormalizeWhitespace(element.Value));
            case "br":
                return "  \n";
            case "a":
                return RenderAnchor(element, state);
            case "img":
                return RenderImgTag(element, state);
            case "sup": case "sub":
                state.Report.Add(new ConversionIssue(
                    IssueSeverity.Lossy,
                    IssueCategory.LossyTransform,
                    $"<{element.Name.LocalName}> (superscript/subscript) has no Markdown representation and was flattened to plain text.",
                    state.CurrentLocation));
                return RenderInline(element.Nodes(), state);
            case "span":
                // Confluence uses <span> purely for styling hooks (highlight colour, etc.);
                // none of that survives into Markdown, so just keep the text.
                return RenderInline(element.Nodes(), state);
            default:
                state.Report.Add(new ConversionIssue(
                    IssueSeverity.Lossy,
                    IssueCategory.DroppedElement,
                    $"Unrecognized inline element <{element.Name.LocalName}> was unwrapped; any formatting it applied was lost, but its text was kept.",
                    state.CurrentLocation,
                    Snippet(element)));
                return RenderInline(element.Nodes(), state);
        }
    }

    private static string Wrap(string content, string marker) =>
        string.IsNullOrEmpty(content) ? content : $"{marker}{content}{marker}";

    private static string WrapCode(string content) =>
        string.IsNullOrEmpty(content) ? content : $"`{content}`";

    private static string RenderAnchor(XElement element, RenderState state)
    {
        var href = (string?)element.Attribute("href");
        var text = RenderInlineTrimmed(element.Nodes(), state);
        if (string.IsNullOrEmpty(text))
        {
            text = href ?? string.Empty;
        }

        return string.IsNullOrEmpty(href) ? text : $"[{text}]({MarkdownText.EscapeLinkUrl(href)})";
    }

    private static string RenderImgTag(XElement element, RenderState state)
    {
        var src = (string?)element.Attribute("src") ?? string.Empty;
        var alt = (string?)element.Attribute("alt") ?? string.Empty;
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Info,
            IssueCategory.LossyTransform,
            "Raw <img> element referenced an external URL rather than a Confluence attachment; passed through as-is — verify it still resolves once migrated.",
            state.CurrentLocation,
            src));
        return $"![{MarkdownText.Escape(alt)}]({src})";
    }
}
