using System.Text.RegularExpressions;

namespace RocketWiki.Core.Content;

/// <summary>
/// The <c>:name:</c> grammar for custom emojis: lowercase <c>[a-z0-9_-]</c>, 1–64
/// characters. Deliberately narrow:
///
/// <list type="bullet">
/// <item>Lowercase-only makes case-insensitive uniqueness structural rather than
/// collational — no mixed-case name can ever enter the registry, so the unique index
/// on Name behaves identically on SQL Server (CI collation) and SQLite (binary), and
/// lookups stay exact/ordinal, the same instinct as design.md §6.3's rule matching.</item>
/// <item>A candidate <c>:name:</c> in content is an emoji only when the name exists in
/// the registry; an unknown name renders as the literal characters — no error, no
/// placeholder. That single rule is also the whole replica/import story (design.md
/// §12/§13): definitions are instance-local, content is plain text everywhere.</item>
/// <item>64 is a display-key cap (matches AttributeDefinition.Key), far below any
/// storage concern.</item>
/// </list>
///
/// This class is the single definition both the write path (registry mutations refuse
/// invalid names) and the phase-2 SPA (autocomplete/renderer matching) port from.
/// </summary>
public static partial class EmojiName
{
    public const int MaxLength = 64;

    /// <summary>The grammar, verbatim — the cross-layer contract the SPA's renderer
    /// must match exactly (same pattern discipline as the heading-anchor algorithm,
    /// design.md §9.2, just far smaller).</summary>
    public const string Pattern = "^[a-z0-9_-]{1,64}$";

    [GeneratedRegex(Pattern)]
    private static partial Regex ValidName();

    public static bool IsValid(string? name) =>
        name is not null && name.Length <= MaxLength && ValidName().IsMatch(name);
}
