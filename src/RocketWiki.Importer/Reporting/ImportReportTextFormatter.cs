using System.Text;
using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Reporting;

/// <summary>
/// Renders an <see cref="ImportReport"/>/<see cref="ImportValidationSummary"/> pair as
/// plain text for a content owner to read — a real migration's report is long enough that
/// nobody reads it in a terminal, so the CLI writes this to a file rather than only
/// printing it (see <c>RocketWiki.Importer.Cli</c>). Pages needing attention are surfaced
/// first and in full; pages with nothing wrong are listed once, briefly, at the end.
/// </summary>
public static class ImportReportTextFormatter
{
    public static string Format(string spaceKey, bool isDryRun, ImportReport report, ImportValidationSummary summary)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"RocketWiki Confluence import report — space '{spaceKey}'");
        sb.AppendLine(isDryRun
            ? "Mode: DRY RUN — nothing was created or written anywhere."
            : "Mode: IMPORT — the space and pages below were actually created.");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:u}");
        sb.AppendLine();

        AppendSummary(sb, summary);

        if (report.PipelineNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== Pipeline notes ==");
            foreach (var note in report.PipelineNotes)
            {
                sb.AppendLine($"- {note}");
            }
        }

        var needsReview = report.Pages.Where(NeedsPageReview).ToList();
        var clean = report.Pages.Except(needsReview).ToList();

        sb.AppendLine();
        sb.AppendLine($"== Pages needing review ({needsReview.Count}) ==");
        if (needsReview.Count == 0)
        {
            sb.AppendLine("(none — every page converted cleanly)");
        }
        else
        {
            foreach (var page in needsReview)
            {
                AppendPage(sb, page, isDryRun);
            }
        }

        if (clean.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"== Pages with no issues ({clean.Count}) ==");
            foreach (var page in clean.OrderBy(p => p.Title, StringComparer.Ordinal))
            {
                var labelSuffix = page.LabelsApplied.Count > 0 ? $" [labels: {string.Join(", ", page.LabelsApplied)}]" : string.Empty;
                sb.AppendLine($"- {page.Title} (Confluence id {page.ConfluencePageId})" +
                    (page.RocketWikiPageId is { } id ? $" -> {id}" : string.Empty) + labelSuffix);
            }
        }

        return sb.ToString();
    }

    private static bool NeedsPageReview(PageImportOutcome page) =>
        page.SkippedReason is not null
        || page.AttachmentFailures.Count > 0
        || page.LabelFailures.Count > 0
        || page.ProducedEmptyContent
        || page.ConversionReport?.HasLossyIssues == true
        || page.Comments.Any(c => c.SkippedReason is not null || c.ConversionReport?.HasLossyIssues == true);

    private static void AppendSummary(StringBuilder sb, ImportValidationSummary summary)
    {
        sb.AppendLine("== Summary ==");
        sb.AppendLine($"Pages in export:                 {summary.TotalPagesInExport}");
        sb.AppendLine($"Pages imported:                  {summary.PagesActuallyImported}");
        sb.AppendLine($"Pages skipped:                   {summary.SkippedPageCount}");
        sb.AppendLine($"Pages with no converted content: {summary.PagesWithNoConvertedContent}");
        sb.AppendLine($"Comments in export:              {summary.TotalCommentsInExport}");
        sb.AppendLine($"Comments imported:               {summary.CommentsActuallyImported}");
        sb.AppendLine($"Comments skipped:                {summary.SkippedCommentCount}");
        sb.AppendLine($"Label failures:                  {summary.LabelFailureCount}");
        sb.AppendLine($"Lossy conversion issues:         {summary.LossyIssueCount}");
        sb.AppendLine($"Informational issues:            {summary.InfoIssueCount}");
        sb.AppendLine($"Unresolvable links:              {summary.UnresolvableLinkCount}");

        if (summary.UnsupportedMacroCounts.Count > 0)
        {
            sb.AppendLine("Unsupported macros:");
            foreach (var (name, count) in summary.UnsupportedMacroCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  - {name}: {count}");
            }
        }
    }

    private static void AppendPage(StringBuilder sb, PageImportOutcome page, bool includeMarkdown)
    {
        sb.AppendLine();
        sb.AppendLine($"--- {page.Title} (Confluence id {page.ConfluencePageId}) ---");
        if (page.RocketWikiPageId is { } id)
        {
            sb.AppendLine($"RocketWiki page id: {id}");
        }

        if (page.OriginalAuthor is not null)
        {
            sb.AppendLine($"Original author: {page.OriginalAuthor} (not yet applied — see design.md §13/§12 on shadow users)");
        }

        if (page.SkippedReason is not null)
        {
            sb.AppendLine($"SKIPPED: {page.SkippedReason}");
        }

        if (page.ProducedEmptyContent)
        {
            sb.AppendLine("WARNING: this page converted to empty content.");
        }

        foreach (var failure in page.AttachmentFailures)
        {
            sb.AppendLine($"Attachment failure: {failure}");
        }

        foreach (var failure in page.LabelFailures)
        {
            sb.AppendLine($"Label failure: {failure}");
        }

        if (page.LabelsApplied.Count > 0)
        {
            sb.AppendLine($"Labels: {string.Join(", ", page.LabelsApplied)}");
        }

        if (page.ConversionReport is not null && page.ConversionReport.Issues.Count > 0)
        {
            sb.AppendLine("Conversion issues:");
            foreach (var issue in page.ConversionReport.Issues)
            {
                AppendIssueLine(sb, issue);
            }
        }

        if (includeMarkdown && !string.IsNullOrEmpty(page.ConvertedMarkdown))
        {
            sb.AppendLine("Converted Markdown preview:");
            sb.AppendLine("  " + page.ConvertedMarkdown.Replace("\n", "\n  "));
        }

        if (page.Comments.Count > 0)
        {
            sb.AppendLine($"Comments ({page.Comments.Count}):");
            foreach (var comment in page.Comments)
            {
                AppendComment(sb, comment, includeMarkdown);
            }
        }
    }

    private static void AppendComment(StringBuilder sb, CommentImportOutcome comment, bool includeMarkdown)
    {
        var status = comment.SkippedReason is not null
            ? $"SKIPPED: {comment.SkippedReason}"
            : comment.RocketWikiCommentId is { } id
                ? $"-> {id}"
                : string.Empty;
        sb.AppendLine($"  * comment {comment.ConfluenceCommentId} {status}".TrimEnd());

        if (comment.OriginalAuthor is not null)
        {
            sb.AppendLine($"    Original author: {comment.OriginalAuthor}");
        }

        if (comment.ConversionReport is not null && comment.ConversionReport.Issues.Count > 0)
        {
            foreach (var issue in comment.ConversionReport.Issues)
            {
                sb.Append("  ");
                AppendIssueLine(sb, issue);
            }
        }

        if (includeMarkdown && !string.IsNullOrEmpty(comment.ConvertedMarkdown))
        {
            sb.AppendLine("    Preview: " + comment.ConvertedMarkdown.Replace("\n", " "));
        }
    }

    private static void AppendIssueLine(StringBuilder sb, ConversionIssue issue)
    {
        var location = issue.Location is null ? string.Empty : $" [{issue.Location}]";
        sb.AppendLine($"  [{issue.Severity}/{issue.Category}]{location} {issue.Message}");
    }
}
