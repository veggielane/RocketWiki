using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Turns a Confluence page title into a slug matching data-model.md's Page.Slug
/// constraint: unique among live siblings. Uniqueness is the caller's responsibility to
/// enforce via <paramref name="alreadyUsed"/> — this mutates that set.
///
/// <para><b>This is a third slug implementation, deliberately kept separate.</b> The
/// other two are the SPA's <c>web/src/pages/pageSlug.ts</c> (what a user gets when they
/// create a page) and <c>HeadingAnchors</c> (section anchors, a genuine cross-language
/// contract with its own fixture corpus). This one differs from the SPA's in ways that
/// are easy to trip over: non-alphanumerics become <c>-</c> here and are DELETED there
/// ("Don't Panic" → <c>don-t-panic</c> vs <c>dont-panic</c>); the cap is 150 with a
/// hard cut here and 80 on a word boundary there; diacritics are NFD-stripped to their
/// base letter here (<c>café</c> → <c>cafe</c>) and dropped there; and an empty result
/// falls back to <c>page-{hex}</c> here while the SPA simply blocks creation.</para>
///
/// <para><b>Unifying them was considered and rejected.</b> A slug is immutable once
/// written and is the page's address, so changing any generator changes URLs for
/// content already imported — a real cost against no user-visible benefit, since the
/// two generators never compete for the same page. What matters is only that both
/// produce a slug in the same CANONICAL form (lowercase, §17), which they do and which
/// the case-insensitive lookup tests enforce. Add a fourth generator and this stops
/// being true; do not.</para>
/// </summary>
internal static partial class Slugifier
{
    public static string Slugify(string title, HashSet<string> alreadyUsed)
    {
        var baseSlug = BuildBaseSlug(title);
        if (alreadyUsed.Add(baseSlug))
        {
            return baseSlug;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }
        while (!alreadyUsed.Add(candidate));

        return candidate;
    }

    private static string BuildBaseSlug(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue; // strips diacritics picked up by NFD: "café" -> "cafe"
            }

            if (ch < 128 && char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append('-');
            }
        }

        var collapsed = CollapseDashes().Replace(sb.ToString(), "-").Trim('-');
        if (collapsed.Length == 0)
        {
            // Non-Latin titles (Cyrillic, CJK, Arabic, ...) strip down to nothing above -
            // fall back to a short, content-derived token instead of a generic "page"
            // that would collide across every such title under the same parent.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title)));
            return "page-" + hash[..8].ToLowerInvariant();
        }

        return collapsed.Length > 150 ? collapsed[..150].TrimEnd('-') : collapsed;
    }

    [GeneratedRegex("-{2,}")]
    private static partial Regex CollapseDashes();
}
