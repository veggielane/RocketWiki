using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Tests.Pipeline;

public class ConfluenceImportValidatorCommentsAndLabelsTests
{
    private static ConfluenceExportPage Page(
        string id, string? parentId, string title, string body,
        IReadOnlyList<ConfluenceExportComment>? comments = null, IReadOnlyList<string>? labels = null) =>
        new(id, parentId, title, body, Author: null, CreatedAtUtc: null, Attachments: [], comments, labels);

    private static ConfluenceExportComment Comment(string id, string? parentId, string body) =>
        new(id, parentId, body, Author: null, CreatedAtUtc: null);

    [Fact]
    public void Validate_previews_a_threaded_comment_reply_with_its_parents_local_id()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments:
            [
                Comment("400", null, "<p>Root.</p>"),
                Comment("401", "400", "<p>Reply.</p>"),
            ]),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        var home = Assert.Single(result.Report.Pages);
        Assert.Equal(2, home.Comments.Count);
        Assert.All(home.Comments, c => Assert.NotNull(c.RocketWikiCommentId));
    }

    [Fact]
    public void Validate_reports_an_orphaned_comment_thread_without_crashing()
    {
        // "400" and "401" claim each other as parent - neither is reachable from a root comment.
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments:
            [
                Comment("400", "401", "<p>a</p>"),
                Comment("401", "400", "<p>b</p>"),
            ]),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        var home = Assert.Single(result.Report.Pages);
        Assert.Equal(2, home.Comments.Count);
        Assert.All(home.Comments, c => Assert.NotNull(c.SkippedReason));
        Assert.Equal(2, result.Summary.SkippedCommentCount);
    }

    [Fact]
    public void Validate_lists_labels_as_would_apply_since_nothing_is_actually_created()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", labels: ["how-to", "important"]),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        var home = Assert.Single(result.Report.Pages);
        Assert.Equal(["how-to", "important"], home.LabelsApplied);
        Assert.Empty(home.LabelFailures);
    }

    [Fact]
    public void Validate_flags_a_lossy_comment_in_the_summary_the_same_way_a_lossy_page_would_be()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments: [Comment("400", null, "<p><u>underlined</u></p>")]),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(1, result.Summary.LossyIssueCount);
        Assert.True(result.Report.NeedsReview);
    }
}
