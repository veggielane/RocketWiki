using System.Text.RegularExpressions;

namespace RocketWiki.Core.Content;

/// <summary>
/// design.md §4/§8: mentions are serialized in Markdown as <c>@[display](user://{id})</c>
/// (the exact form the editor's mentionRule.ts emits — see web/src/editor/markdown) and
/// "parsed from user://{id} links on save". This parser is the single backend reader of
/// that form.
///
/// Only ids that parse as GUIDs are returned: the id inside <c>user://</c> is a local
/// <c>User.Id</c>, and a non-GUID id (hand-typed Markdown, imported content) cannot name
/// a local user, so there is no one to notify — dropped silently rather than erroring a
/// save over a decorative link. The display text is deliberately NOT returned: it is a
/// client-side snapshot, and nothing downstream may trust it over the Users table.
/// </summary>
public static partial class MentionParser
{
    // Mirrors MENTION_RE in web/src/editor/markdown/mentionRule.ts: display text is
    // anything up to the closing bracket, the id anything up to ')' or whitespace.
    [GeneratedRegex(@"@\[[^\]]*\]\(user://([^)\s]+)\)")]
    private static partial Regex MentionPattern();

    /// <summary>Distinct mentioned user ids, in first-appearance order. Empty for null/empty markdown.</summary>
    public static IReadOnlyList<Guid> ExtractMentionedUserIds(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("user://", StringComparison.Ordinal))
        {
            return [];
        }

        var seen = new HashSet<Guid>();
        var result = new List<Guid>();
        foreach (Match match in MentionPattern().Matches(markdown))
        {
            if (Guid.TryParse(match.Groups[1].Value, out var userId) && seen.Add(userId))
            {
                result.Add(userId);
            }
        }

        return result;
    }

    /// <summary>
    /// design.md §8: mention notifications are <b>delta-based on edit</b> — the users
    /// mentioned in <paramref name="currentMarkdown"/> who were NOT already mentioned in
    /// <paramref name="previousMarkdown"/>, so re-saving content never re-pings its
    /// standing mentions. The one delta rule for every mention-bearing surface: page
    /// saves diff against the previous revision's content, comment edits against the
    /// comment's pre-edit body. A null/empty <paramref name="previousMarkdown"/> (first
    /// revision, empty original) means every current mention is new.
    /// </summary>
    public static HashSet<Guid> ExtractNewlyMentionedUserIds(string? currentMarkdown, string? previousMarkdown)
    {
        var newlyMentioned = ExtractMentionedUserIds(currentMarkdown).ToHashSet();
        if (newlyMentioned.Count > 0)
        {
            newlyMentioned.ExceptWith(ExtractMentionedUserIds(previousMarkdown));
        }

        return newlyMentioned;
    }
}
