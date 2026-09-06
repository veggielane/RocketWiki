using System.Text.RegularExpressions;

namespace RocketWiki.Core.Content;

/// <summary>
/// Finds the pages a page's Markdown links to: every <c>page://{guid}</c> the editor's
/// page-link mark emits (<c>web/src/editor/marks/PageLink.ts</c>, round-tripped by
/// <c>toMarkdown</c>/<c>fromMarkdown</c>). The <c>Page.linkTargets</c> field resolves
/// exactly this set and nothing a client could supply (design.md §6.7 / §21.8): a root
/// field taking arbitrary ids would be an unaudited existence-and-marking oracle for any
/// id a caller obtained elsewhere, whereas ids read out of content the caller can
/// already see disclose only what an author already linked.
///
/// <para>Distinct, in first-occurrence order — the order the links appear in the page —
/// so a client can pair targets with anchors positionally. Anything after
/// <c>page://</c> that is not a well-formed GUID is skipped: it names no page, and
/// echoing it back would be echoing content.</para>
///
/// <para>Lives in Core because it has two readers with one definition between them:
/// <c>Page.linkTargets</c> scans the content per request, and the page link index
/// (<see cref="Entities.PageLink"/>) is written from this same output on every content
/// write and backfilled by migration to match it. Two scanners would be two opinions
/// about what a page links to.</para>
/// </summary>
public static partial class PageLinkScanner
{
    [GeneratedRegex(
        @"page://([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PageLink();

    public static IReadOnlyList<Guid> Extract(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var seen = new HashSet<Guid>();
        var ordered = new List<Guid>();
        foreach (Match match in PageLink().Matches(content))
        {
            if (Guid.TryParseExact(match.Groups[1].Value, "D", out var id) && seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }
}
