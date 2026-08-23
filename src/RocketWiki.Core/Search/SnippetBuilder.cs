namespace RocketWiki.Core.Search;

/// <summary>
/// Builds the excerpt a search hit shows under its title. Two defensive rules,
/// both deliberate (design.md §6.7/§9):
///
/// 1. <b>Plain text only, never markup.</b> No <c>&lt;b&gt;</c> highlight tags, no
///    HTML escaping — emitting markup from server-side string surgery is exactly how
///    half-escaped fragments end up rendered or injected. The client receives an
///    inert string and renders it as text; highlighting, if ever wanted, is the
///    client's job over text it already treats as text.
/// 2. <b>Only ever called with content the caller may view.</b> The search service
///    invokes this strictly after canView passes for the page; nothing here sees
///    restricted content, so nothing here can leak it.
///
/// Excerpts are cut on char boundaries with surrogate-pair protection (never split
/// an astral-plane character in half), whitespace-collapsed (a Markdown source's
/// line structure is noise in a one-line snippet), and ellipsized on the truncated
/// side(s) only.
/// </summary>
public static class SnippetBuilder
{
    public const int DefaultLength = 200;

    /// <summary>
    /// Earliest case-insensitive occurrence in <paramref name="content"/> of any
    /// whitespace-delimited term of <paramref name="query"/> (quotes stripped, so an
    /// FTS-style "quoted phrase" still locates its words). Returns -1 when nothing
    /// matches — e.g. a title-only hit, or an FTS stem ("running" matched "ran")
    /// this literal scan can't see; callers then fall back to a leading excerpt.
    /// </summary>
    public static int LocateFirstMatch(string content, string query, out int matchLength)
    {
        matchLength = 0;
        if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(query))
        {
            return -1;
        }

        var best = -1;
        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim('"');
            if (term.Length == 0)
            {
                continue;
            }

            var index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
                matchLength = term.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Excerpt of up to <paramref name="maxLength"/> chars centered on the match at
    /// <paramref name="matchIndex"/> (pass 0,0 for a plain leading excerpt).
    /// </summary>
    public static string Build(string content, int matchIndex, int matchLength, int maxLength = DefaultLength)
    {
        if (string.IsNullOrEmpty(content) || maxLength <= 0)
        {
            return string.Empty;
        }

        matchIndex = Math.Clamp(matchIndex, 0, content.Length);
        matchLength = Math.Clamp(matchLength, 0, content.Length - matchIndex);

        var start = Math.Max(0, matchIndex + (matchLength / 2) - (maxLength / 2));
        var end = Math.Min(content.Length, start + maxLength);
        start = Math.Max(0, end - maxLength); // re-anchor when the window hit the end

        // Never cut a surrogate pair in half — a torn half-character is exactly the
        // kind of "half-escaped" garbage this builder exists to never produce.
        if (start > 0 && char.IsLowSurrogate(content[start]))
        {
            start--;
        }

        if (end < content.Length && end > 0 && char.IsHighSurrogate(content[end - 1]))
        {
            end--;
        }

        var excerpt = CollapseWhitespace(content.AsSpan(start, end - start));

        if (start > 0)
        {
            excerpt = "…" + excerpt;
        }

        if (end < content.Length)
        {
            excerpt += "…";
        }

        return excerpt;
    }

    private static string CollapseWhitespace(ReadOnlySpan<char> text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0; // also trims leading whitespace
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
