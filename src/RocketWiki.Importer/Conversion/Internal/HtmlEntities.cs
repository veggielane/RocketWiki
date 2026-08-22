using System.Text.RegularExpressions;

namespace RocketWiki.Importer.Conversion.Internal;

/// <summary>
/// Decodes named HTML character entities that real Confluence exports commonly contain
/// (typeset punctuation, currency symbols) but that are not valid on their own in XML —
/// XML only predefines <c>amp</c>, <c>lt</c>, <c>gt</c>, <c>apos</c>, <c>quot</c>. Numeric
/// character references (<c>&amp;#160;</c>, <c>&amp;#x2014;</c>) are already valid XML and
/// need no help here.
/// </summary>
/// <remarks>
/// This list is deliberately not exhaustive. An entity outside it is left untouched, which
/// makes the subsequent XML parse fail with a clear <see cref="ConfluenceConversionException"/>
/// naming the page — the honest failure mode for an entity this converter has never seen,
/// rather than silently dropping or mis-rendering it.
/// </remarks>
internal static partial class HtmlEntities
{
    private static readonly Dictionary<string, char> Named = new(StringComparer.Ordinal)
    {
        ["nbsp"] = ' ',
        ["mdash"] = '—',
        ["ndash"] = '–',
        ["hellip"] = '…',
        ["rsquo"] = '’',
        ["lsquo"] = '‘',
        ["rdquo"] = '”',
        ["ldquo"] = '“',
        ["copy"] = '©',
        ["reg"] = '®',
        ["trade"] = '™',
        ["deg"] = '°',
        ["plusmn"] = '±',
        ["times"] = '×',
        ["divide"] = '÷',
        ["euro"] = '€',
        ["pound"] = '£',
        ["cent"] = '¢',
        ["sect"] = '§',
        ["para"] = '¶',
        ["middot"] = '·',
        ["laquo"] = '«',
        ["raquo"] = '»',
        ["bull"] = '•',
    };

    private static readonly HashSet<string> XmlNative = new(StringComparer.Ordinal)
    {
        "amp", "lt", "gt", "apos", "quot",
    };

    [GeneratedRegex("&([a-zA-Z][a-zA-Z0-9]*);")]
    private static partial Regex NamedEntityPattern();

    public static string Decode(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('&'))
        {
            return input;
        }

        return NamedEntityPattern().Replace(input, match =>
        {
            var name = match.Groups[1].Value;
            if (XmlNative.Contains(name))
            {
                return match.Value;
            }

            return Named.TryGetValue(name, out var decoded) ? decoded.ToString() : match.Value;
        });
    }
}
