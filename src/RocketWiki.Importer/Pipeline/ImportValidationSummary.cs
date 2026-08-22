namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Whole-space rollup over an <see cref="ImportReport"/> — the "read this first" numbers
/// for both a dry run and a real import, computed identically for both so the two are
/// directly comparable (design.md §16: a dry run should predict what a real run will do).
/// Issue counts (<see cref="LossyIssueCount"/>, etc.) include comment bodies as well as
/// page bodies — a lossy comment is exactly as real as a lossy page.
/// </summary>
public sealed record ImportValidationSummary(
    int TotalPagesInExport,
    int SkippedPageCount,
    int PagesWithNoConvertedContent,
    int InfoIssueCount,
    int LossyIssueCount,
    int UnresolvableLinkCount,
    IReadOnlyDictionary<string, int> UnsupportedMacroCounts,
    int TotalCommentsInExport = 0,
    int SkippedCommentCount = 0,
    int LabelFailureCount = 0)
{
    public int PagesActuallyImported => TotalPagesInExport - SkippedPageCount;

    public int CommentsActuallyImported => TotalCommentsInExport - SkippedCommentCount;
}
