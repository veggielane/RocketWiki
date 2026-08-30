using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Turns a Confluence page title into a slug matching data-model.md's Page.Slug
/// constraint: unique among live siblings. Uniqueness is the caller's responsibility to
/// enforce via <paramref name="alreadyUsed"/> — this mutates that set.
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
