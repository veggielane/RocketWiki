using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Tests.Pipeline.Fakes;

namespace RocketWiki.Importer.Tests.Pipeline;

public class ConfluenceSpaceImporterCommentsAndLabelsTests
{
    private readonly FakeSpaceService _spaceService = new();
    private readonly FakePageService _pageService = new();
    private readonly FakeAttachmentService _attachmentService = new();
    private readonly FakeCommentService _commentService = new();
    private readonly FakeLabelService _labelService = new();

    private ConfluenceSpaceImporter CreateImporter() =>
        new(_spaceService, _pageService, _attachmentService, _commentService, _labelService);

    private static ImportOptions DefaultOptions() => new(
        Principal.Create("importer-sub", ["importers"]),
        Guid.NewGuid(),
        new AuditContext(AuditChannel.System, "test-import", "127.0.0.1"),
        new InitialSpaceGrant(SpaceRole.SpaceAdmin, """{ "everyone": true }"""));

    private static ConfluenceExportPage Page(
        string id, string? parentId, string title, string body,
        IReadOnlyList<ConfluenceExportComment>? comments = null, IReadOnlyList<string>? labels = null) =>
        new(id, parentId, title, body, Author: null, CreatedAtUtc: null, Attachments: [], comments, labels);

    private static ConfluenceExportComment Comment(string id, string? parentId, string body, ConfluenceExportAuthor? author = null) =>
        new(id, parentId, body, author, CreatedAtUtc: null);

    [Fact]
    public async Task Top_level_comment_is_created_with_no_parent()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments: [Comment("400", null, "<p>Nice page!</p>")]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        var commentOutcome = Assert.Single(outcome.Comments);
        Assert.NotNull(commentOutcome.RocketWikiCommentId);
        Assert.Null(commentOutcome.SkippedReason);
        Assert.Null(_commentService.AddCalls.Single().ParentCommentId);
    }

    [Fact]
    public async Task Threaded_reply_is_created_after_its_parent_with_the_parents_real_id()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments:
            [
                Comment("400", null, "<p>Root comment.</p>"),
                Comment("401", "400", "<p>A reply.</p>"),
            ]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        var root = outcome.Comments.Single(c => c.ConfluenceCommentId == "400");
        var reply = outcome.Comments.Single(c => c.ConfluenceCommentId == "401");
        Assert.NotNull(root.RocketWikiCommentId);
        Assert.NotNull(reply.RocketWikiCommentId);

        var replyRequest = _commentService.AddCalls.Single(r => r.Body == "A reply.\n");
        Assert.Equal(root.RocketWikiCommentId, replyRequest.ParentCommentId);
    }

    [Fact]
    public async Task Comment_whose_parent_failed_is_skipped_and_never_attempted()
    {
        _commentService.FailAddWhen = req => req.Body.Contains("Root") ? new ValidationError("boom") : null;

        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments:
            [
                Comment("400", null, "<p>Root comment.</p>"),
                Comment("401", "400", "<p>A reply.</p>"),
            ]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        var root = outcome.Comments.Single(c => c.ConfluenceCommentId == "400");
        var reply = outcome.Comments.Single(c => c.ConfluenceCommentId == "401");
        Assert.Contains("comment creation failed", root.SkippedReason);
        Assert.Contains("parent comment failed", reply.SkippedReason);
        Assert.DoesNotContain(_commentService.AddCalls, r => r.Body.Contains("reply"));
    }

    [Fact]
    public async Task Comment_body_resolves_page_links_through_the_same_resolver_as_pages()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>"),
            Page("2", null, "Target", "<p>Target page.</p>",
                comments: [Comment("400", null, """<p>See <ac:link><ri:page ri:content-title="Home" /></ac:link>.</p>""")]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var homeOutcome = result.Report.Pages.Single(p => p.Title == "Home");
        var targetOutcome = result.Report.Pages.Single(p => p.Title == "Target");
        var commentOutcome = Assert.Single(targetOutcome.Comments);
        var addedComment = _commentService.AddCalls.Single(r => r.Body.Contains("page://"));
        Assert.Contains($"page://{homeOutcome.RocketWikiPageId}", addedComment.Body);
        Assert.NotNull(commentOutcome.RocketWikiCommentId);
    }

    [Fact]
    public async Task A_lossy_comment_marks_the_report_as_needing_review()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments: [Comment("400", null, "<p><u>underlined</u></p>")]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.True(result.Report.NeedsReview);
        Assert.Equal(1, result.Summary.LossyIssueCount);
    }

    [Fact]
    public async Task Label_used_on_two_pages_is_created_once_and_attached_twice()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Page A", "<p>a</p>", labels: ["how-to"]),
            Page("2", null, "Page B", "<p>b</p>", labels: ["how-to"]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.Single(_labelService.CreateCalls);
        Assert.Equal(2, _labelService.AttachCalls.Count);
        Assert.All(result.Report.Pages, p => Assert.Contains("how-to", p.LabelsApplied));
    }

    [Fact]
    public async Task Label_creation_failure_is_reported_and_not_attached()
    {
        _labelService.FailCreateWhen = _ => new ValidationError("Label 'how-to' already exists in this space.");

        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Page A", "<p>a</p>", labels: ["how-to"]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.Empty(outcome.LabelsApplied);
        Assert.Contains(outcome.LabelFailures, f => f.Contains("how-to"));
        Assert.Empty(_labelService.AttachCalls);
        Assert.Equal(1, result.Summary.LabelFailureCount);
    }

    [Fact]
    public async Task Label_attach_failure_is_reported_separately_from_creation_failure()
    {
        _labelService.FailAttachWhen = _ => new ForbiddenError("canEdit required");

        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Page A", "<p>a</p>", labels: ["how-to"]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.Empty(outcome.LabelsApplied);
        Assert.Contains(outcome.LabelFailures, f => f.Contains("could not attach"));
    }

    [Fact]
    public async Task Summary_counts_total_and_skipped_comments_across_the_space()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", comments:
            [
                Comment("400", null, "<p>One.</p>"),
                Comment("401", null, "<p>Two.</p>"),
            ]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.Equal(2, result.Summary.TotalCommentsInExport);
        Assert.Equal(0, result.Summary.SkippedCommentCount);
        Assert.Equal(2, result.Summary.CommentsActuallyImported);
    }
}
