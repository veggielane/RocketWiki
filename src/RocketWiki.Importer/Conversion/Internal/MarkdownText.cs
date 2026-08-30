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
    /// Schemes a link or image URL from the imported source is allowed to carry. Anything
    /// else — <c>javascript:</c>, <c>data:</c>, <c>vbscript:</c>, <c>file:</c> and the rest
    /// — is not escaped into safety by <see cref="EscapeLinkUrl"/>, which is breakout-safe
    /// but scheme-blind, so it needs refusing on its own terms.
    /// </summary>
    private static readonly string[] AllowedLinkSchemes = ["http", "https", "mailto", "ftp", "tel"];

    /// <summary>
    /// Whether a URL from the Confluence source may be emitted as a Markdown destination.
    ///
    /// <para><b>This is importer-side hygiene, not the last line of defence</b> — and it is
    /// written that way deliberately. The SPA already neutralises both shapes today: the
    /// link mark blanks <c>javascript:</c>/<c>data:</c> hrefs, and the image node renders
    /// only <c>attachment://</c> sources. But a converter whose output is safe only because
    /// of what the renderer happens to do is a converter that has made the renderer's
    /// behaviour part of its contract without saying so. Stored content should not contain
    /// <c>[click](javascript:…)</c> in the first place.</para>
    ///
    /// <para>Relative URLs, fragments and protocol-relative URLs carry no scheme and are
    /// allowed: they are ordinary wiki links, and they cannot execute. A "scheme" is only
    /// recognised as one when the colon is preceded by a valid scheme name (RFC 3986:
    /// letter, then letters/digits/<c>+ - .</c>) — so <c>page 1: notes</c> is text, not a
    /// scheme, and <c>C:\share\file</c> is not either.</para>
    /// </summary>
    public static bool IsAllowedLinkUrl(string url)
    {
        var trimmed = url.Trim();

        // Control characters (including the tab/newline a browser strips before parsing a
        // URL) are how "java\tscript:" gets past a naive prefix check. Nothing legitimate
        // needs them in a destination.
        if (trimmed.Any(char.IsControl))
        {
            return false;
        }

        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return true; // no scheme at all: relative, fragment, or "//host/path"
        }

        // A '/', '?' or '#' before the colon means the colon is inside a path or query,
        // not a scheme delimiter.
        var beforeColon = trimmed.AsSpan(0, colon);
        if (beforeColon.IndexOfAny('/', '?', '#') >= 0)
        {
            return true;
        }

        if (!char.IsAsciiLetter(beforeColon[0]))
        {
            return true; // not a well-formed scheme, so not a scheme
        }

        foreach (var c in beforeColon[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return true;
            }
        }

        return AllowedLinkSchemes.Contains(beforeColon.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A fenced code block's info string, reduced to something that can only ever BE an
    /// info string.
    ///
    /// <para>A Confluence code macro carries whatever <c>language</c> its author typed, and
    /// it was interpolated straight after the opening fence. A value containing a newline
    /// therefore closed the info string and everything after it became live Markdown blocks
    /// in stored content — headings, lists, tables, links — attributable to nobody. Not
    /// XSS (the renderer takes ProseMirror JSON, never HTML) but a structure-spoofing
    /// primitive an imported page's original author controls.</para>
    ///
    /// <para>Reduced rather than escaped: an info string has no escaping mechanism, so the
    /// only safe answer is to keep the characters a language tag legitimately uses
    /// (letters, digits, <c>+ - . # _</c> — <c>#</c> because <c>c#</c> is one) and drop the
    /// rest. A value with nothing left is no language at all, which renders an untagged
    /// fence — the harmless direction, and the same one a reserved language already
    /// degrades to. Length-capped for the same reason: whatever survives ends up on the
    /// fence's opening line, and a language tag that is longer than
    /// <see cref="MaxFenceLanguageLength"/> characters is not a language tag.</para>
    /// </summary>
    public static string? SanitizeFenceLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var sb = new StringBuilder(language.Length);
        foreach (var c in language.Trim())
        {
            if (sb.Length == MaxFenceLanguageLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.' or '#' or '_')
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>The longest fence info string this importer will emit. "objective-c++" is
    /// 13; nothing legitimate approaches this.</summary>
    public const int MaxFenceLanguageLength = 32;

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
