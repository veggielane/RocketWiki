using System.Text.RegularExpressions;

namespace RocketWiki.Core.Search;

/// <summary>One heading as the anchor algorithm sees it: level (1-6) and plain text content.</summary>
public readonly record struct HeadingInfo(int Level, string Text);

/// <summary>
/// design.md §9: "the anchor algorithm is a cross-language contract." This is a
/// line-by-line PORT of <c>web/src/editor/headingAnchors.ts</c> (the canonical
/// implementation — the frontend authored it first, and the ids it stamps onto
/// rendered headings are what these anchors must land on), NOT an independent
/// slugifier: two independently written slugifiers agree on ordinary headings and
/// diverge on the edge cases (punctuation, non-ASCII fallback, duplicate-path
/// ordinals), silently breaking exactly the deep links people complain about.
/// Verified against the shared fixture corpus in <c>tests/fixtures/heading-anchors/</c>
/// (HeadingAnchorsCorpusTests) — the corpus is regenerated from the TypeScript
/// implementation on every frontend test run, so agreement here is agreement with
/// the real thing, not with a hand-copied expectation.
///
/// Do not "improve" the algorithm on one side only. Change the TypeScript first,
/// let the corpus regenerate, and let the corpus test here fail loudly.
/// </summary>
public static partial class HeadingAnchors
{
    /// <summary>
    /// Port of <c>slugifySegment</c>: lowercase, trim, keep only <c>[a-z0-9\s-]</c>,
    /// whitespace runs to single hyphens, collapse hyphen runs, trim edge hyphens,
    /// and fall back to the literal string "section" when nothing survives (the
    /// non-ASCII / punctuation-only cases in the corpus).
    /// </summary>
    public static string SlugifySegment(string text)
    {
        var slug = text.ToLowerInvariant().Trim();
        slug = DisallowedCharacters().Replace(slug, string.Empty);
        slug = WhitespaceRun().Replace(slug, "-");
        slug = HyphenRun().Replace(slug, "-");
        slug = slug.Trim('-');
        return slug.Length > 0 ? slug : "section";
    }

    /// <summary>
    /// Port of <c>computeHeadingPaths</c>: walks headings in document order and returns
    /// each heading's breadcrumb path — ancestor heading texts by level nesting, ending
    /// with its own text.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ComputeHeadingPaths(IReadOnlyList<HeadingInfo> headings)
    {
        var stack = new List<HeadingInfo>();
        var paths = new List<IReadOnlyList<string>>(headings.Count);

        foreach (var heading in headings)
        {
            while (stack.Count > 0 && stack[^1].Level >= heading.Level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add(heading);
            paths.Add(stack.Select(h => h.Text).ToArray());
        }

        return paths;
    }

    /// <summary>Port of <c>slugifyPath</c>: each segment slugified, joined with "--".</summary>
    public static string SlugifyPath(IReadOnlyList<string> path) =>
        string.Join("--", path.Select(SlugifySegment));

    /// <summary>
    /// Port of <c>computeHeadingAnchors</c>, the public entry point: headings in document
    /// order in, one anchor id per heading out, duplicate-path disambiguation applied
    /// (first exact duplicate keeps the bare slug, later ones get <c>-2</c>, <c>-3</c>, …).
    /// </summary>
    public static IReadOnlyList<string> ComputeHeadingAnchors(IReadOnlyList<HeadingInfo> headings)
    {
        var paths = ComputeHeadingPaths(headings);
        var seen = new Dictionary<string, int>();
        var anchors = new List<string>(paths.Count);

        foreach (var path in paths)
        {
            var baseSlug = SlugifyPath(path);
            var occurrence = seen.TryGetValue(baseSlug, out var count) ? count + 1 : 1;
            seen[baseSlug] = occurrence;
            anchors.Add(occurrence == 1 ? baseSlug : $"{baseSlug}-{occurrence}");
        }

        return anchors;
    }

    // The TS source's /[^a-z0-9\s-]/g. JS \s and .NET \s differ at the margins
    // (JS includes U+FEFF, .NET includes U+0085) — headings containing either are
    // vanishingly rare, and the corpus test is the arbiter if one ever shows up.
    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex DisallowedCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex("-+")]
    private static partial Regex HyphenRun();
}
