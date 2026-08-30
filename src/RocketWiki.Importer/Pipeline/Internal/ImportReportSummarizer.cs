using RocketWiki.Importer.Conversion;

namespace RocketWiki.Importer.Pipeline.Internal;

internal static class ImportReportSummarizer
{
    public static ImportValidationSummary Summarize(ImportReport report, int totalPagesInExport)
    {
        var pageIssues = report.Pages
            .Where(p => p.ConversionReport is not null)
            .SelectMany(p => p.ConversionReport!.Issues);
        var commentIssues = report.Pages
            .SelectMany(p => p.Comments)
            .Where(c => c.ConversionReport is not null)
            .SelectMany(c => c.ConversionReport!.Issues);
        var allIssues = pageIssues.Concat(commentIssues).ToList();

        var unsupportedMacroCounts = allIssues
            .Where(i => i.Category == IssueCategory.UnsupportedMacro && i.Detail is not null)
            .GroupBy(i => i.Detail!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var allComments = report.Pages.SelectMany(p => p.Comments).ToList();

        return new ImportValidationSummary(
            TotalPagesInExport: totalPagesInExport,
            SkippedPageCount: report.Pages.Count(p => p.SkippedReason is not null),
            PagesWithNoConvertedContent: report.Pages.Count(p => p.ProducedEmptyContent),
            InfoIssueCount: allIssues.Count(i => i.Severity == IssueSeverity.Info),
            LossyIssueCount: allIssues.Count(i => i.Severity == IssueSeverity.Lossy),
            UnresolvableLinkCount: allIssues.Count(i => i.Category == IssueCategory.UnresolvedLink),
            UnsupportedMacroCounts: unsupportedMacroCounts,
            TotalCommentsInExport: allComments.Count,
            SkippedCommentCount: allComments.Count(c => c.SkippedReason is not null),
            LabelFailureCount: report.Pages.Sum(p => p.LabelFailures.Count),
            SourceSpacePermissionCount: report.SourceSpacePermissions.Count,
            SourcePageRestrictionCount: report.Pages.Sum(p => p.SourceRestrictions.Count));
    }
}
