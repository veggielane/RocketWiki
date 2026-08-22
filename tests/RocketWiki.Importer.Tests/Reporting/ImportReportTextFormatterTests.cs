using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Reporting;

namespace RocketWiki.Importer.Tests.Reporting;

public class ImportReportTextFormatterTests
{
    private static ImportValidationSummary EmptySummary(int total = 0) =>
        new(total, 0, 0, 0, 0, 0, new Dictionary<string, int>());

    [Fact]
    public void Format_includes_the_space_key_and_mode()
    {
        var text = ImportReportTextFormatter.Format("ENG", isDryRun: true, new ImportReport(), EmptySummary());

        Assert.Contains("space 'ENG'", text);
        Assert.Contains("DRY RUN", text);
    }

    [Fact]
    public void Format_reports_real_import_mode_distinctly_from_dry_run()
    {
        var text = ImportReportTextFormatter.Format("ENG", isDryRun: false, new ImportReport(), EmptySummary());

        Assert.DoesNotContain("DRY RUN", text);
        Assert.Contains("IMPORT", text);
    }

    [Fact]
    public void Format_surfaces_summary_totals()
    {
        var summary = new ImportValidationSummary(10, 2, 1, 3, 4, 5, new Dictionary<string, int> { ["gallery"] = 2 });

        var text = ImportReportTextFormatter.Format("ENG", true, new ImportReport(), summary);

        Assert.Contains("Pages in export:                 10", text);
        Assert.Contains("Pages skipped:                   2", text);
        Assert.Contains("gallery: 2", text);
    }

    [Fact]
    public void Format_lists_a_skipped_page_under_needs_review_with_its_reason()
    {
        var report = new ImportReport();
        report.AddPage(new PageImportOutcome("1", "Broken Page", null, null, [], null, "page creation failed: boom"));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("Pages needing review (1)", text);
        Assert.Contains("Broken Page", text);
        Assert.Contains("SKIPPED: page creation failed: boom", text);
    }

    [Fact]
    public void Format_lists_a_clean_page_under_pages_with_no_issues_and_not_under_needs_review()
    {
        var report = new ImportReport();
        report.AddPage(new PageImportOutcome("1", "Clean Page", Guid.NewGuid(), new ConversionReport(), [], null, null));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("Pages needing review (0)", text);
        Assert.Contains("Pages with no issues (1)", text);
        Assert.Contains("Clean Page", text);
    }

    [Fact]
    public void Format_includes_conversion_issue_detail_with_severity_category_and_location()
    {
        var conversionReport = new ConversionReport();
        conversionReport.Add(new ConversionIssue(IssueSeverity.Lossy, IssueCategory.LossyTransform, "Underline removed.", "Setup > Prereqs"));

        var report = new ImportReport();
        report.AddPage(new PageImportOutcome("1", "Setup", Guid.NewGuid(), conversionReport, [], null, null));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("[Lossy/LossyTransform] [Setup > Prereqs] Underline removed.", text);
    }

    [Fact]
    public void Format_includes_the_converted_markdown_preview_only_in_dry_run_mode()
    {
        var conversionReport = new ConversionReport();
        conversionReport.Add(new ConversionIssue(IssueSeverity.Lossy, IssueCategory.LossyTransform, "Underline removed."));

        var report = new ImportReport();
        report.AddPage(new PageImportOutcome("1", "Page", Guid.NewGuid(), conversionReport, [], null, null, ConvertedMarkdown: "underlined text\n"));

        var dryRunText = ImportReportTextFormatter.Format("ENG", true, report, EmptySummary(1));
        var realRunText = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("underlined text", dryRunText);
        Assert.DoesNotContain("underlined text", realRunText);
    }

    [Fact]
    public void Format_flags_attachment_failures_and_empty_content_pages()
    {
        var report = new ImportReport();
        report.AddPage(new PageImportOutcome(
            "1", "Page With Bad Attachment", Guid.NewGuid(), new ConversionReport(), ["'diagram.png': storage unavailable"], null, null));
        report.AddPage(new PageImportOutcome(
            "2", "Empty Page", Guid.NewGuid(), new ConversionReport(), [], null, null, ProducedEmptyContent: true));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(2));

        Assert.Contains("Attachment failure: 'diagram.png': storage unavailable", text);
        Assert.Contains("WARNING: this page converted to empty content.", text);
        Assert.Contains("Pages needing review (2)", text);
    }

    [Fact]
    public void Format_shows_a_skipped_comment_under_its_page()
    {
        var report = new ImportReport();
        report.AddPage(new PageImportOutcome(
            "1", "Home", Guid.NewGuid(), new ConversionReport(), [], null, null,
            Comments: [new CommentImportOutcome("400", null, null, null, "comment creation failed: boom")]));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("Comments (1):", text);
        Assert.Contains("comment 400", text);
        Assert.Contains("SKIPPED: comment creation failed: boom", text);
        Assert.Contains("Pages needing review (1)", text);
    }

    [Fact]
    public void Format_lists_applied_labels_on_both_review_and_clean_pages()
    {
        var report = new ImportReport();
        report.AddPage(new PageImportOutcome(
            "1", "Clean Page", Guid.NewGuid(), new ConversionReport(), [], null, null, LabelsApplied: ["how-to"]));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("[labels: how-to]", text);
    }

    [Fact]
    public void Format_shows_label_failures_and_counts_the_page_as_needing_review()
    {
        var report = new ImportReport();
        report.AddPage(new PageImportOutcome(
            "1", "Home", Guid.NewGuid(), new ConversionReport(), [], null, null,
            LabelFailures: ["'how-to': could not create (already exists)"]));

        var text = ImportReportTextFormatter.Format("ENG", false, report, EmptySummary(1));

        Assert.Contains("Label failure: 'how-to': could not create (already exists)", text);
        Assert.Contains("Pages needing review (1)", text);
    }
}
