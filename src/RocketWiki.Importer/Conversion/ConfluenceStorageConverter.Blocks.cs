using System.Text.RegularExpressions;
using System.Xml.Linq;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Conversion;

public sealed partial class ConfluenceStorageConverter
{
    private static readonly HashSet<string> KnownInlineLocalNames =
    [
        "strong", "b", "em", "i", "s", "strike", "del", "u", "code", "br", "a", "img", "sup", "sub", "span",
    ];

    [GeneratedRegex(@"^(?:[-*+] |\d+[.)] )")]
    private static partial Regex ListMarkerStart();

    /// <summary>Renders a sequence of sibling nodes as a list of block-level Markdown strings.</summary>
    private static List<string> RenderBlocks(IEnumerable<XNode> nodes, RenderState state)
    {
        var blocks = new List<string>();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                {
                    // Whitespace-only text between block elements (pretty-printed XML) is
                    // formatting noise, not content — skip it without reporting anything.
                    var normalized = MarkdownText.NormalizeWhitespace(text.Value);
                    if (normalized.Length > 0)
                    {
                        blocks.Add(MarkdownText.EscapeLeadingBlockMarker(MarkdownText.Escape(normalized)));
                    }

                    break;
                }

                case XElement element:
                {
                    var block = RenderBlockElement(element, state);
                    if (block is not null)
                    {
                        blocks.Add(block);
                    }

                    break;
                }

                // XComment, XProcessingInstruction, etc. carry no content — dropped silently.
            }
        }

        return blocks;
    }

    private static string? RenderBlockElement(XElement element, RenderState state)
    {
        if (element.Name.Namespace == ConfluenceNamespaces.Ac)
        {
            return RenderAcBlockElement(element, state);
        }

        switch (element.Name.LocalName.ToLowerInvariant())
        {
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                return RenderHeading(element, state);
            case "p":
                return RenderParagraph(element, state);
            case "ul":
                return RenderList(element, state, ordered: false);
            case "ol":
                return RenderList(element, state, ordered: true);
            case "blockquote":
                return RenderBlockquote(element, state);
            case "table":
                return RenderTable(element, state);
            case "pre":
                return RenderPre(element);
            case "hr":
                return "---";
            case "div":
                // Confluence sometimes wraps content in a plain <div> (e.g. exported from a
                // page layout cell); it carries no meaning of its own, so unwrap it.
                return JoinBlocks(RenderBlocks(element.Nodes(), state));
            default:
                if (KnownInlineLocalNames.Contains(element.Name.LocalName.ToLowerInvariant()))
                {
                    // An inline element used without a wrapping <p> — a structural quirk, not
                    // a loss, since RenderInline still applies its normal formatting/escaping.
                    return RenderInlineTrimmed([element], state);
                }

                return RenderUnrecognizedElement(element, state);
        }
    }

    private static string? RenderUnrecognizedElement(XElement element, RenderState state)
    {
        var text = MarkdownText.NormalizeWhitespace(element.Value);
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.DroppedElement,
            text.Length > 0
                ? $"Unrecognized element <{element.Name.LocalName}> was dropped; its text content was kept as plain text but any structure or behavior it carried was lost."
                : $"Unrecognized element <{element.Name.LocalName}> was dropped; it had no extractable text content.",
            state.CurrentLocation,
            Snippet(element)));

        return text.Length == 0 ? null : MarkdownText.EscapeLeadingBlockMarker(MarkdownText.Escape(text));
    }

    private static string RenderHeading(XElement element, RenderState state)
    {
        var level = element.Name.LocalName[1] - '0';
        var text = RenderInlineTrimmed(element.Nodes(), state);
        state.EnterHeading(level, MarkdownText.NormalizeWhitespace(element.Value));
        return new string('#', level) + " " + text;
    }

    private static string? RenderParagraph(XElement element, RenderState state)
    {
        var text = RenderInlineTrimmed(element.Nodes(), state);
        return string.IsNullOrWhiteSpace(text) ? null : MarkdownText.EscapeLeadingBlockMarker(text);
    }

    private static string RenderBlockquote(XElement element, RenderState state)
    {
        var combined = JoinBlocks(RenderBlocks(element.Nodes(), state));
        return MarkdownText.PrefixLines(combined, "> ");
    }

    private static string RenderPre(XElement element)
    {
        var content = element.Value.Trim('\r', '\n');
        var fence = FenceFor(content);
        return $"{fence}\n{content}\n{fence}";
    }

    private static string RenderList(XElement element, RenderState state, bool ordered)
    {
        var items = element.Elements().Where(e => e.HasLocalName("li")).ToList();
        var rendered = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var marker = ordered ? $"{i + 1}. " : "- ";
            var combined = JoinBlocksForListItem(RenderBlocks(items[i].Nodes(), state));
            rendered.Add(ApplyMarker(combined, marker));
        }

        return string.Join('\n', rendered);
    }

    /// <summary>
    /// Joins a list item's own blocks (its text plus, commonly, a nested sub-list). A
    /// nested list is joined with no blank line before it — CommonMark recognizes a list
    /// marker as an unambiguous block start on its own, so no blank line is needed to
    /// separate it from preceding text, and design.md §4's canonical normalization
    /// flattens loose lists (a blank line here) to tight ones on the editor's first save
    /// regardless. Emitting tight directly avoids shipping Markdown that would silently
    /// reformat the moment someone opens and saves the page. Every other block-to-block
    /// transition (two paragraphs, a paragraph then a blockquote) keeps its blank line:
    /// those aren't unambiguous block starts, and losing the blank line risks the second
    /// block being read as a lazy continuation of the first instead of its own block.
    /// </summary>
    private static string JoinBlocksForListItem(IReadOnlyList<string> blocks)
    {
        var nonEmpty = blocks.Where(b => !string.IsNullOrEmpty(b)).ToList();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < nonEmpty.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(ListMarkerStart().IsMatch(nonEmpty[i]) ? "\n" : "\n\n");
            }

            sb.Append(nonEmpty[i]);
        }

        return sb.ToString();
    }

    /// <summary>Prefixes a (possibly multi-line, multi-block) list item body with its marker,
    /// indenting continuation lines so they align under the first line's text.</summary>
    private static string ApplyMarker(string block, string marker)
    {
        var lines = block.Split('\n');
        var pad = new string(' ', marker.Length);
        var sb = new System.Text.StringBuilder();
        sb.Append(marker).Append(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            if (lines[i].Length > 0)
            {
                sb.Append(pad).Append(lines[i]);
            }
        }

        return sb.ToString();
    }

    private static string JoinBlocks(IReadOnlyList<string> blocks) =>
        string.Join("\n\n", blocks.Where(b => !string.IsNullOrEmpty(b)));

    private static string FenceFor(string content)
    {
        var longestRun = 0;
        var current = 0;
        foreach (var ch in content)
        {
            if (ch == '`')
            {
                current++;
                longestRun = Math.Max(longestRun, current);
            }
            else
            {
                current = 0;
            }
        }

        return new string('`', Math.Max(3, longestRun + 1));
    }

    private static string Snippet(XElement element)
    {
        var raw = element.ToString(SaveOptions.DisableFormatting);
        return raw.Length > 200 ? raw[..200] + "…" : raw;
    }
}
