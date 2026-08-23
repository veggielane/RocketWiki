using System.Text.RegularExpressions;

namespace RocketWiki.Core.Search;

/// <summary>One extracted heading: level (1-6), plain text, and the character offset of its line start in the source Markdown.</summary>
public sealed record MarkdownHeading(int Level, string Text, int Offset);

/// <summary>
/// Extracts ATX headings (<c># … ######</c>) from Markdown so search hits can be
/// attributed to the section containing the match (design.md §9: heading-path
/// breadcrumb + anchor deep link). Deliberately a small hand-rolled scanner, not a
/// full Markdown parser:
///
/// - Fenced code blocks (``` / ~~~) are skipped — a <c># comment</c> inside a fence
///   is the classic false heading, and it must not shift every anchor below it.
/// - Setext headings (underlined with === / ---) are NOT recognized. The editor's
///   Markdown serializer only ever emits ATX, so setext can only arrive via import,
///   where the converter normalizes to ATX too.
/// - Inline markup is reduced to the plain text TipTap would render: links/images
///   collapse to their text, backticks and asterisks are dropped. This matters
///   because the frontend computes anchors from the *rendered* heading's text
///   content; leaving <c>[Setup](https://…)</c> as-is would slugify the URL into
///   the anchor and silently break the deep-link contract. Underscores are kept:
///   they are far more often literal (<c>snake_case</c> identifiers) than emphasis
///   in this corpus, and the slugifier strips them either way, so the anchor is
///   identical in both readings — only the displayed breadcrumb text differs.
///
/// The heading <c>Text</c> here feeds <see cref="HeadingAnchors"/> (the
/// cross-language contract) and the user-visible <c>SearchHit.headingPath</c>.
/// </summary>
public static partial class MarkdownHeadings
{
    public static IReadOnlyList<MarkdownHeading> Extract(string markdown)
    {
        var headings = new List<MarkdownHeading>();
        if (string.IsNullOrEmpty(markdown))
        {
            return headings;
        }

        var inFence = false;
        var lineStart = 0;

        while (lineStart <= markdown.Length - 1)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            var nextLineStart = lineEnd < 0 ? markdown.Length : lineEnd + 1;
            var line = markdown[lineStart..(lineEnd < 0 ? markdown.Length : lineEnd)].TrimEnd('\r');

            if (FenceLine().IsMatch(line))
            {
                // A plain toggle: an unclosed fence swallows the rest of the document,
                // which is also exactly what a Markdown renderer does with it.
                inFence = !inFence;
            }
            else if (!inFence)
            {
                var match = AtxHeading().Match(line);
                if (match.Success)
                {
                    headings.Add(new MarkdownHeading(
                        match.Groups["hashes"].Length,
                        CleanInline(match.Groups["text"].Value),
                        lineStart));
                }
            }

            lineStart = nextLineStart;
        }

        return headings;
    }

    private static string CleanInline(string text)
    {
        // CommonMark's optional closing hash sequence: "## Title ##" -> "Title".
        text = ClosingHashes().Replace(text, string.Empty);
        text = ImageSyntax().Replace(text, "$1");
        text = LinkSyntax().Replace(text, "$1");
        text = text.Replace("`", string.Empty).Replace("*", string.Empty);
        return text.Trim();
    }

    // Up to 3 leading spaces, 1-6 hashes, then either end-of-line (empty heading,
    // which the anchor algorithm maps to the "section" fallback) or whitespace + text.
    [GeneratedRegex(@"^ {0,3}(?<hashes>#{1,6})(?:[ \t]+(?<text>.*))?$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(```|~~~)")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$")]
    private static partial Regex ClosingHashes();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageSyntax();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkSyntax();
}
