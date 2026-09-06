namespace RocketWiki.Core.Tests.Content;

/// <summary>
/// One corpus of Markdown edge cases for the page link index, shared by every tier
/// (linked into RocketWiki.Data.Tests and RocketWiki.SqlServer.Tests, the way
/// <c>TestCatalogs</c> is): the Core tier pins the scanner's answer for each case by hand,
/// the Data tier pins that the incremental maintainer writes exactly
/// <c>PageLink.FromContent</c> for it, and the SQL Server tier pins that the
/// <c>AddPageLinks</c> backfill produces the same rows from the same content. Three
/// definitions of "what a page links to" (regex, EF, T-SQL) are held to one corpus so
/// they cannot diverge one edge case at a time.
/// </summary>
public static class PageLinkCorpus
{
    public static readonly Guid A = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    public static readonly Guid B = Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");

    /// <summary>Named by no page anywhere — the dangling target every tier must store.</summary>
    public static readonly Guid Dangling = Guid.Parse("cccccccc-3333-4333-8333-cccccccccccc");

    /// <summary>
    /// More links in one page than SQL Server's default recursion limit (100): the
    /// backfill walks occurrences recursively and must declare <c>MAXRECURSION 0</c> or
    /// fail the migration on a page like this one.
    /// </summary>
    public const int ManyLinkCount = 120;

    public static Guid Many(int i) => new(0x10000 + i, 0x1111, 0x4222, [0x81, 2, 3, 4, 5, 6, 7, 8]);

    public static string Link(Guid id) => $"[link](page://{id:D})";

    /// <summary>A case: its content and the distinct targets, in order, the scanner must find.</summary>
    public sealed record Case(string Name, string Content, IReadOnlyList<Guid> Expected);

    public static IReadOnlyList<Case> Cases { get; } =
    [
        new("two-links-with-a-repeat", $"{Link(A)} and {Link(B)} then {Link(A)} again", [A, B]),
        new("upper-and-lower-spellings-are-one-target",
            $"page://{A.ToString("D").ToUpperInvariant()} and page://{A.ToString("D").ToLowerInvariant()}", [A]),
        new("scheme-is-case-sensitive", $"PAGE://{B:D} Page://{B:D}", []),
        new("not-a-guid", "page://not-a-guid and page://12345678 and page://", []),
        new("braced-form-is-not-a-link", $"page://{B:B}", []),
        new("hex-run-on-after-the-guid-still-matches", $"page://{A:D}abcdef", [A]),
        new("truncated-guid", $"page://{A.ToString("D")[..35]}", []),
        new("newline-inside-the-guid", $"page://aaaaaaaa-\n1111-4111-8111-aaaaaaaaaaaa", []),
        new("adjacent-schemes", $"page://page://{A:D}", [A]),
        new("dangling-target-is-still-a-link", $"{Link(Dangling)}", [Dangling]),
        new("other-schemes-are-not-page-links", $"@[Ada](user://{A:D}) ![x](attachment://{B:D})", []),
        new("empty", "", []),
        new("many-links-beyond-the-default-recursion-cap",
            string.Join("\n", Enumerable.Range(0, ManyLinkCount).Select(i => Link(Many(i)))),
            Enumerable.Range(0, ManyLinkCount).Select(Many).ToArray()),
    ];
}
