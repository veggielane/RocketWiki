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

        // Said once, at the top, because this file is not what it looks like. A dry
        // run’s report contains every page’s converted Markdown and every original
        // author’s email address — the content of the space, in plaintext, at whatever
        // path --report named, with no marking and no access control. The wiki it is
        // about may be export-controlled; the report is not, and nothing else says so.
        sb.AppendLine("HANDLING: this report reproduces page content and author email addresses in");
        sb.AppendLine("plaintext. It carries no protective marking of its own and nothing restricts");
        sb.AppendLine("who can read the file. Treat it as at least as sensitive as the space it");
        sb.AppendLine("describes, and delete it when the migration review is finished.");
        sb.AppendLine();

        AppendSummary(sb, summary);
        AppendSourcePermissions(sb, report);

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
        || page.SourceRestrictions.Count > 0
        || page.ProducedEmptyContent
        || page.ConversionReport?.HasLossyIssues == true
        || page.Comments.Any(c => c.SkippedReason is not null || c.ConversionReport?.HasLossyIssues == true);

    /// <summary>
    /// design.md §13's other half. The importer takes a required initial grant and applies
    /// nothing from the source, which is only safe if the operator is told what the source
    /// had — otherwise a page five people could read lands in a space the whole grant can
    /// read, and no artefact anywhere records that a restriction ever existed.
    ///
    /// <para>Placed immediately after the summary, above conversion issues, because it is
    /// the only section describing something that is <b>wrong right now in production</b>
    /// rather than something that converted imperfectly. Printed even when empty: "the
    /// export carried no permissions" and "nobody looked for any" have to be
    /// distinguishable, and before this section existed every report read like the
    /// first.</para>
    /// </summary>
    private static void AppendSourcePermissions(StringBuilder sb, ImportReport report)
    {
        var restrictions = report.SourcePageRestrictions.ToList();

        sb.AppendLine();
        sb.AppendLine("== Confluence permissions found in the export ==");
        sb.AppendLine("NONE OF THESE WERE APPLIED. RocketWiki does not translate Confluence");
        sb.AppendLine("permissions (design.md §13): the imported space is governed solely by the");
        sb.AppendLine("grant expression given on the command line. Re-apply anything below");
        sb.AppendLine("deliberately, as space grants (§6.3) or page restrictions (§6.4), BEFORE");
        sb.AppendLine("telling users the space is ready.");
        sb.AppendLine();
        sb.AppendLine("Every name below is a CONFLUENCE group or account, reproduced verbatim from");
        sb.AppendLine("the export. None of them was looked up here and none implies a RocketWiki");
        sb.AppendLine("group of the same name. Permission types are Confluence's own, including any");
        sb.AppendLine("with no RocketWiki equivalent — they are listed under their Confluence names");
        sb.AppendLine("rather than mapped to something that looks close.");
        sb.AppendLine();

        if (report.SourceSpacePermissions.Count == 0 && restrictions.Count == 0)
        {
            sb.AppendLine("The export carried no space permissions and no page restrictions.");
            return;
        }

        sb.AppendLine($"Space permissions ({report.SourceSpacePermissions.Count}):");
        if (report.SourceSpacePermissions.Count == 0)
        {
            sb.AppendLine("  (none in the export)");
        }
        else
        {
            // Grouped by Confluence permission type, because that is the unit an admin
            // re-applies: "who could view this space" is one decision about one list of
            // subjects, not N unrelated lines to reassemble by eye.
            foreach (var group in report.SourceSpacePermissions
                .GroupBy(p => string.IsNullOrWhiteSpace(p.Type) ? "(no type recorded)" : p.Type, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {group.Key} ({group.Count()}):");
                foreach (var permission in group)
                {
                    sb.AppendLine($"    - {DescribeSubject(permission)}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Page restrictions ({restrictions.Count} across {restrictions.Select(r => r.PageTitle).Distinct(StringComparer.Ordinal).Count()} page(s)):");
        if (restrictions.Count == 0)
        {
            sb.AppendLine("  (none in the export)");
        }
        else
        {
            foreach (var group in restrictions.GroupBy(r => r.PageTitle, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {group.Key}:");
                foreach (var (_, restriction) in group)
                {
                    sb.AppendLine($"    - {Describe(restriction)}");
                }
            }
        }
    }

    /// <summary>
    /// Verbatim, including a type this importer has never heard of — the reader does not
    /// filter by a known-types list precisely so that an unfamiliar permission reaches
    /// the person deciding what to do about it.
    /// </summary>
    private static string Describe(Export.ConfluenceExportPermission permission)
    {
        var type = string.IsNullOrWhiteSpace(permission.Type) ? "(no type recorded)" : permission.Type;
        return $"{type} → {DescribeSubject(permission)}";
    }

    /// <summary>
    /// Every subject is labelled <b>Confluence</b>. These strings name groups and accounts
    /// in the source system and mean nothing on this instance — no RocketWiki group is
    /// implied, and none is looked up. Saying so on each line is what stops
    /// "propulsion-engineers" being read as a group that exists here.
    /// </summary>
    private static string DescribeSubject(Export.ConfluenceExportPermission permission) =>
        permission.SubjectKind switch
        {
            "anonymous" => "ANONYMOUS (Confluence recorded no group and no user) — RocketWiki has no anonymous access at all, so this one has no equivalent to re-apply",
            "group" => $"Confluence group '{permission.Subject ?? "(unnamed)"}'",
            "user" => $"Confluence user '{permission.Subject ?? "(unnamed)"}'",
            null => permission.Subject is null ? "(no subject recorded)" : $"Confluence subject '{permission.Subject}'",
            _ => $"Confluence {permission.SubjectKind} '{permission.Subject ?? "(unnamed)"}'",
        };

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
        sb.AppendLine($"Confluence permissions found:    {summary.SourcePermissionCount} (space: {summary.SourceSpacePermissionCount}, page: {summary.SourcePageRestrictionCount}) - NONE APPLIED");

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

        if (page.SourceRestrictions.Count > 0)
        {
            // Repeated here as well as in the report-level section: whoever is working
            // through one page's issues needs to see that this page was restricted at
            // source without having to hold a list from three screens earlier.
            sb.AppendLine($"RESTRICTED IN CONFLUENCE — not applied here ({page.SourceRestrictions.Count}):");
            foreach (var restriction in page.SourceRestrictions)
            {
                sb.AppendLine($"  - {Describe(restriction)}");
            }
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

        // Also shown on a REAL import when the page did not land: PageImportOutcome’s
        // own doc says the converted Markdown is carried so you can see what would
        // have been saved when the save itself failed, and gating it purely on
        // isDryRun made that stated purpose unreachable — exactly the case where the
        // text is the only copy left outside the export.
        if ((includeMarkdown || page.SkippedReason is not null) && !string.IsNullOrEmpty(page.ConvertedMarkdown))
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

    /// <summary>
    /// Prints <see cref="ConversionIssue.Detail"/> as well as the message. The converter
    /// populates Detail at every issue site with the thing a reviewer needs — the source
    /// snippet that was dropped, the macro name, the URL that may not resolve — and the
    /// report used to discard all of it. "Unrecognized element was dropped" without the
    /// snippet, and "verify this <c>&lt;img&gt;</c> still resolves" without the URL, are
    /// both unactionable: the reviewer has to go back to the export and find it by hand.
    /// </summary>
    private static void AppendIssueLine(StringBuilder sb, ConversionIssue issue)
    {
        var location = issue.Location is null ? string.Empty : $" [{issue.Location}]";
        sb.AppendLine($"  [{issue.Severity}/{issue.Category}]{location} {issue.Message}");

        if (!string.IsNullOrWhiteSpace(issue.Detail))
        {
            // Collapsed to one line and bounded: a Detail can be a whole dropped element,
            // and a report nobody can scroll through is its own kind of unreadable.
            var detail = issue.Detail!.ReplaceLineEndings(" ").Trim();
            const int MaxDetail = 300;
            if (detail.Length > MaxDetail)
            {
                detail = detail[..MaxDetail] + "… (truncated)";
            }

            sb.AppendLine($"      detail: {detail}");
        }
    }
}
