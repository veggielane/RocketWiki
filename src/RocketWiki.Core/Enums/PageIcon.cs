namespace RocketWiki.Core.Enums;

/// <summary>
/// The icon a page may carry, shown beside its title in the navigation tree and on
/// the page itself.
///
/// <para>A closed vocabulary rather than free text, for the same reason the
/// classification ladder and the space roles are: the SPA renders each member from a
/// mapping it owns, so it can never be handed an icon it has no glyph for. A
/// free-form string would make "what does this page look like" depend on whatever a
/// client happened to store, and a replica could receive an icon its build has never
/// heard of.</para>
///
/// <para>Purely presentational. Nothing reads an icon to make a decision — not the
/// rule engine, not search, not the clearance gate — so an unrecognised value is a
/// display problem and never an access one. That is why it is nullable and why the
/// absent case is simply "no icon" rather than a default that would assert something
/// about the page.</para>
///
/// <para>Stored and synced by NAME, never by number (see
/// <see cref="PageIcons.ToWireName"/>). The numeric values here are an implementation
/// detail of one process; inserting a member in the middle must not silently repaint
/// every page in the database or arrive at a replica meaning something else.</para>
/// </summary>
public enum PageIcon
{
    Document = 1,
    Book = 2,
    Note = 3,
    Checklist = 4,
    Calendar = 5,
    Chart = 6,
    Database = 7,
    Code = 8,
    Terminal = 9,
    Bug = 10,
    Flask = 11,
    Rocket = 12,
    Wrench = 13,
    Cloud = 14,
    Lock = 15,
    Shield = 16,
    Warning = 17,
    Lightbulb = 18,
    People = 19,
    Map = 20,
    Star = 21,
    Flag = 22,
}

/// <summary>
/// The wire form of <see cref="PageIcon"/> — the member name, upper-snake-cased, which
/// is also exactly how it appears in the GraphQL schema. One converter, used by the
/// EF column mapping and the sync bundle alike, so a page's icon reads the same in the
/// database, on the wire and in the API.
/// </summary>
public static class PageIcons
{
    /// <summary>Longest wire name, plus room — the EF column bound.</summary>
    public const int MaxWireNameLength = 32;

    public static string ToWireName(PageIcon icon) => ToUpperSnake(icon.ToString());

    /// <summary>
    /// Parses a wire name back, returning null for anything unrecognised rather than
    /// throwing. An icon is decoration: a bundle from a newer instance carrying an icon
    /// this build has never heard of must import the page without it, not fail the
    /// import and strand every other change in the bundle behind it.
    /// </summary>
    public static PageIcon? FromWireName(string? wireName)
    {
        if (string.IsNullOrWhiteSpace(wireName))
        {
            return null;
        }

        foreach (var candidate in Enum.GetValues<PageIcon>())
        {
            if (string.Equals(ToWireName(candidate), wireName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ToUpperSnake(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
