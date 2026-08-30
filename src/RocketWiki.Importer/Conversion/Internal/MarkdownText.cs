using System.Text;

namespace RocketWiki.Importer.Conversion.Internal;

/// <summary>
/// Text-level helpers shared by block and inline rendering: HTML-style whitespace
/// collapsing, Markdown special-character escaping, and line-prefixing for blockquotes,
/// callouts, and list-item continuations.
/// </summary>
internal static class MarkdownText
{
    /// <summary>
    /// Collapses runs of whitespace (including newlines from pretty-printed XML) into a
    /// single space, without trimming the ends. Used for text nodes that sit alongside
    /// inline elements, where a trailing/leading space is meaningful — trimming per text
    /// node would silently swallow the space between "word" and a following
    /// <c>&lt;strong&gt;</c> or macro. Callers that know they hold a *complete* piece of
    /// inline content (a whole paragraph, a heading) trim once at that point instead.
    /// </summary>
    public static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// <see cref="CollapseWhitespace"/> plus a trim of both ends — for text known to stand
    /// alone as a complete block (a stray text node at block level, <c>&lt;code&gt;</c>
    /// contents, a heading's plain-text form for the report's location breadcrumb).
    /// </summary>
    public static string NormalizeWhitespace(string text) => CollapseWhitespace(text).Trim();

    private static readonly char[] EscapeChars = ['\\', '`', '*', '_', '[', ']', '<'];

    /// <summary>
    /// Escapes characters that Markdown would otherwise interpret as formatting, so that
    /// literal text copied from Confluence (e.g. "use *bold* to emphasize") survives as
    /// plain text rather than becoming accidental Markdown syntax.
    /// </summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Array.IndexOf(EscapeChars, ch) >= 0)
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a leading character that Markdown would parse as a block marker
    /// (heading, list item, blockquote) if it started a line of its own — relevant when
    /// plain text happens to begin a paragraph, e.g. text that starts with a literal "# ".
    /// </summary>
    /// <remarks>
    /// Deliberately does not include '*' or '_': <see cref="Escape"/> already escapes those
    /// unconditionally wherever they appear in literal text, so a leading '*' reaching this
    /// method can only be *generated* Markdown (e.g. "**bold**" from wrapping emphasis) —
    /// escaping it here would corrupt formatting this converter itself produced.
    /// </remarks>
    public static string EscapeLeadingBlockMarker(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (text[0] is '#' or '-' or '+' or '>')
        {
            return "\\" + text;
        }

        var i = 0;
        while (i < text.Length && char.IsDigit(text[i]))
        {
            i++;
        }

        if (i > 0 && i < text.Length && text[i] is '.' or ')')
        {
            return string.Concat(text.AsSpan(0, i), "\\", text.AsSpan(i));
        }

        return text;
    }

    /// <summary>
    /// Percent-encodes the characters that would otherwise end a Markdown link's URL
    /// span early. Space and <c>)</c> are the common ones — SharePoint and Jira URLs
    /// carry both routinely — but <c>(</c> matters too: CommonMark balances parentheses
    /// inside a destination, so an unmatched opening one swallows the rest of the line.
    /// <c>&lt;</c> would start an autolink, and a raw newline ends the destination
    /// outright.
    ///
    /// <para>Applied to EVERY destination the converter emits, links and images alike.
    /// It used to be applied on the anchor path only, so exactly the URLs most likely to
    /// contain a space — an image pasted from SharePoint — were the ones emitted raw, and
    /// the image silently broke.</para>
    ///
    /// <para><c>%</c> is deliberately NOT escaped: Confluence URLs are routinely
    /// percent-encoded already, so encoding the escape character would turn every
    /// <c>%20</c> that arrives correct into a broken <c>%2520</c>. The cost is that a
    /// literal <c>%</c> stays ambiguous, which breaks nothing that was working.</para>
    /// </summary>
    public static string EscapeLinkUrl(string url) => url
        .Replace(" ", "%20")
        .Replace("(", "%28")
        .Replace(")", "%29")
        .Replace("<", "%3C")
        .Replace(">", "%3E")
        .Replace("\r", string.Empty)
        .Replace("\n", string.Empty);

    /// <summary>
    /// Prefixes every line of a (possibly multi-block) string with <paramref name="prefix"/>,
    /// used for blockquotes and callout directives. Blank lines get the trimmed prefix
    /// (bare "&gt;") so CommonMark treats them as blockquote continuation, not termination.
    /// </summary>
    public static string PrefixLines(string text, string prefix)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].Length == 0 ? prefix.TrimEnd() : prefix + lines[i];
        }

        return string.Join('\n', lines);
    }
}
