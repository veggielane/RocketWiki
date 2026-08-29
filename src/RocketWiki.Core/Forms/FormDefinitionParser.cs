using System.Text.RegularExpressions;

namespace RocketWiki.Core.Forms;

/// <summary>
/// Reads ```` ```form-definition ```` fences out of a page's Markdown and parses them.
///
/// <para>Text in, records out — no database, no permissions, no page. That keeps it
/// testable as a pure function and, more importantly, keeps the grammar in one place
/// that both validation and rendering are served from. A second parser in the SPA would
/// be a second opinion about what a form is.</para>
/// </summary>
public static partial class FormDefinitionParser
{
    /// <summary>The fence language. Reserved: a page may not use it for anything else,
    /// the same way `page-list` and `drawio` are reserved.</summary>
    public const string FenceLanguage = "form-definition";

    /// <summary>Matches a fenced block and captures its language and body. Tolerant of
    /// trailing spaces after the language, which editors add without telling anyone.</summary>
    [GeneratedRegex(@"^```[ \t]*([A-Za-z0-9_-]+)[ \t]*\r?\n(.*?)^```[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FenceRegex { get; }

    /// <summary>`name: type(a, b), required` — the options group and the modifier tail
    /// are both optional.</summary>
    [GeneratedRegex(@"^\s*([^:]+?)\s*:\s*([A-Za-z]+)\s*(?:\(([^)]*)\))?\s*(?:,\s*(.+?))?\s*$")]
    private static partial Regex FieldRegex { get; }

    /// <summary>
    /// Every form declared on a page, in document order, plus every one that could not
    /// be parsed.
    ///
    /// <para>Errors are returned rather than thrown or skipped: a malformed definition is
    /// an authoring mistake, and the author needs to see which collection and what was
    /// wrong. Silently dropping it would present as "my form disappeared".</para>
    ///
    /// <para>A collection declared twice on one page is an error for BOTH declarations,
    /// not a last-one-wins merge: two definitions competing to describe one record set is
    /// exactly how a form quietly changes shape, and neither is more correct than the
    /// other.</para>
    /// </summary>
    public static (IReadOnlyList<FormDefinition> Definitions, IReadOnlyList<FormDefinitionError> Errors)
        Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return ([], []);
        }

        var parsed = new List<FormDefinition>();
        var errors = new List<FormDefinitionError>();

        foreach (Match fence in FenceRegex.Matches(markdown))
        {
            if (!string.Equals(fence.Groups[1].Value, FenceLanguage, StringComparison.Ordinal))
            {
                continue;
            }

            var (definition, error) = ParseBody(fence.Groups[2].Value);
            if (definition is not null)
            {
                parsed.Add(definition);
            }
            else if (error is not null)
            {
                errors.Add(error);
            }
        }

        // Duplicates are found after parsing so both sides can be named. Compared on the
        // normalized collection, because that is what the store compares on — two
        // definitions differing only in case are one collection, not two.
        var duplicated = parsed
            .GroupBy(d => Normalize(d.Collection), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var collection in duplicated)
        {
            errors.Add(new FormDefinitionError(
                collection,
                $"'{collection}' is defined more than once on this page. Two definitions would compete to describe one set of records; rename one."));
        }

        return (parsed.Where(d => !duplicated.Contains(Normalize(d.Collection))).ToList(), errors);
    }

    /// <summary>Lower-cased invariant, matching how the entry store normalizes a
    /// collection — a definition and its records must agree about their own name.</summary>
    public static string Normalize(string collection) => collection.Trim().ToLowerInvariant();

    private static (FormDefinition? Definition, FormDefinitionError? Error) ParseBody(string body)
    {
        string? collection = null;
        var fields = new List<FormField>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (string.Equals(key, "collection", StringComparison.OrdinalIgnoreCase))
            {
                collection = value;
                continue;
            }

            if (!string.Equals(key, "field", StringComparison.OrdinalIgnoreCase))
            {
                // Unknown keys are ignored rather than refused, so a fence written by a
                // newer build degrades on an older one instead of breaking the page.
                continue;
            }

            var match = FieldRegex.Match(value);
            if (!match.Success)
            {
                return (null, new FormDefinitionError(collection ?? "(unnamed)",
                    $"Could not read the field '{value}'. Expected 'name: type' — for example 'summary: text, required'."));
            }

            var name = match.Groups[1].Value.Trim();
            if (!names.Add(name))
            {
                return (null, new FormDefinitionError(collection ?? "(unnamed)",
                    $"The field '{name}' is declared twice."));
            }

            if (!TryParseType(match.Groups[2].Value, out var type))
            {
                return (null, new FormDefinitionError(collection ?? "(unnamed)",
                    $"'{match.Groups[2].Value}' is not a field type. Use text, number, date or select."));
            }

            var options = match.Groups[3].Success
                ? match.Groups[3].Value.Split(',').Select(o => o.Trim()).Where(o => o.Length > 0).ToList()
                : [];

            if (type == FormFieldType.Select && options.Count == 0)
            {
                return (null, new FormDefinitionError(collection ?? "(unnamed)",
                    $"The select field '{name}' lists no options."));
            }

            var required = match.Groups[4].Success
                && match.Groups[4].Value.Split(',').Any(m => string.Equals(m.Trim(), "required", StringComparison.OrdinalIgnoreCase));

            fields.Add(new FormField(name, type, required, options));
        }

        if (string.IsNullOrWhiteSpace(collection))
        {
            return (null, new FormDefinitionError("(unnamed)", "This form has no 'collection = ...' line, so its records have nowhere to live."));
        }

        if (fields.Count == 0)
        {
            return (null, new FormDefinitionError(collection, "This form declares no fields."));
        }

        return (new FormDefinition(collection.Trim(), fields), null);
    }

    private static bool TryParseType(string token, out FormFieldType type)
    {
        switch (token.ToLowerInvariant())
        {
            case "text": type = FormFieldType.Text; return true;
            case "number": type = FormFieldType.Number; return true;
            case "date": type = FormFieldType.Date; return true;
            case "select": type = FormFieldType.Select; return true;
            default: type = FormFieldType.Text; return false;
        }
    }
}
