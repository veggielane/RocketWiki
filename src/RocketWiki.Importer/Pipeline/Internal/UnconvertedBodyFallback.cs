namespace RocketWiki.Importer.Pipeline.Internal;

/// <summary>
/// The body an imported page gets when its Confluence storage format could not be
/// converted at all (design.md §13).
///
/// <para>Three outcomes were available for a page whose body will not parse, and they are
/// not close. Aborting the import is the worst: there is no transaction spanning an
/// import, so it leaves the pages before it committed, no report, and a re-run blocked by
/// the space key. Importing an empty or placeholder page is the second worst — the page
/// exists, looks migrated, and the content is simply gone, which nobody discovers until
/// somebody needs it. Keeping the original body verbatim inside a fenced code block loses
/// nothing: the text is still there to read, to search, and to convert by hand, and the
/// page keeps its place in the tree so its children are still reachable.</para>
///
/// <para>Fenced, not inline: the body is XHTML and must render as text, never as markup.
/// The fence is grown past the longest backtick run in the body, per CommonMark — a
/// Confluence code macro containing three backticks would otherwise close the block early
/// and spill the rest of the page out as live Markdown.</para>
/// </summary>
internal static class UnconvertedBodyFallback
{
    /// <summary>
    /// Leads with the reason, in the page itself. Whoever opens this page next is
    /// probably not the person who ran the import and has no reason to go looking for a
    /// report file.
    /// </summary>
    public static string Build(string rawStorageBodyXhtml, string failureReason)
    {
        var fence = new string('`', LongestBacktickRun(rawStorageBodyXhtml) is var longest && longest >= 3 ? longest + 1 : 3);

        return
            $"""
            > **This page could not be converted from Confluence.**
            >
            > {failureReason}
            >
            > Nothing was lost: the original Confluence storage-format body is preserved
            > verbatim below. It needs converting by hand, and the import report lists this
            > page as a conversion failure.

            {fence}xml
            {rawStorageBodyXhtml}
            {fence}
            """;
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;

        foreach (var c in text)
        {
            if (c == '`')
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }
}
