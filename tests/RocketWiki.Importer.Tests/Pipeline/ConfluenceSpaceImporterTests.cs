using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Tests.Pipeline.Fakes;

namespace RocketWiki.Importer.Tests.Pipeline;

/// <summary>
/// Tests ConfluenceSpaceImporter's orchestration — tree order, two-pass id resolution,
/// attachment sequencing, per-page/pipeline reporting, and error propagation — against
/// fake ISpaceService/IPageService/IAttachmentService implementations. These fakes
/// enforce the same referential rules the real EF-backed services do (a page can't be
/// created under a nonexistent parent, slugs must be unique among siblings), so a bug in
/// the importer's ordering logic fails here the same way it would for real - but this
/// suite does NOT exercise the real services or EF Core.
/// </summary>
public class ConfluenceSpaceImporterTests
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
        ConfluenceExportAuthor? author = null, IReadOnlyList<ConfluenceExportAttachment>? attachments = null) =>
        new(id, parentId, title, body, author, CreatedAtUtc: null, attachments ?? []);

    private static ConfluenceExportAttachment Attachment(string id, string fileName, byte[] bytes, string contentType = "application/octet-stream") =>
        new(id, fileName, contentType, () => new MemoryStream(bytes));

    [Fact]
    public async Task Happy_path_creates_the_page_tree_and_resolves_a_forward_reference_link()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", "Engineering docs",
        [
            Page("1", null, "Home", """<p>See <ac:link><ri:page ri:content-title="Details" /></ac:link>.</p>"""),
            Page("2", "1", "Details", "<p>The details.</p>"),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.True(result.Success);
        Assert.NotNull(result.SpaceId);
        Assert.Equal(2, result.Report.Pages.Count);
        Assert.All(result.Report.Pages, p => Assert.Null(p.SkippedReason));

        var detailsPage = _pageService.CreatedPages.Single(p => p.Title == "Details");
        Assert.Equal(_pageService.CreatedPages.Single(p => p.Title == "Home").Id, detailsPage.ParentPageId);

        var homeOutcome = result.Report.Pages.Single(p => p.Title == "Home");
        var finalHomeContent = _pageService.UpdateCalls.Last(c => c.PageId == homeOutcome.RocketWikiPageId).Content;
        Assert.Contains($"page://{detailsPage.Id}", finalHomeContent);
        Assert.False(homeOutcome.ConversionReport!.HasLossyIssues);
    }

    [Fact]
    public async Task Space_is_created_with_the_options_initial_grant()
    {
        var options = new ImportOptions(
            Principal.Create("importer-sub", ["importers"]),
            Guid.NewGuid(),
            new AuditContext(AuditChannel.System, "test-import", "127.0.0.1"),
            new InitialSpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }"""));

        var export = new ConfluenceExportSpace("ENG", "Engineering", null, [Page("1", null, "Home", "<p>Hi</p>")]);

        await CreateImporter().ImportAsync(export, options);

        var grant = Assert.Single(_spaceService.InitialGrants);
        Assert.Equal(SpaceRole.Editor, grant.Role);
        Assert.Equal("""{ "group": "engineering" }""", grant.ExpressionJson);
    }

    [Fact]
    public async Task Space_creation_failure_blocks_the_import_before_anything_else_is_attempted()
    {
        _spaceService.FailCreateWhen = _ => new ValidationError("Space key 'ENG' is already in use.");

        var export = new ConfluenceExportSpace("ENG", "Engineering", null, [Page("1", null, "Home", "<p>Hi</p>")]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.False(result.Success);
        Assert.Null(result.SpaceId);
        Assert.Contains("ENG", result.BlockedReason);
        Assert.Empty(_pageService.CreateCalls);
        Assert.Empty(_attachmentService.UploadCalls);
    }

    [Fact]
    public async Task A_failed_page_skips_its_whole_subtree_without_ever_attempting_to_create_the_children()
    {
        _pageService.FailCreateWhen = req => req.Title == "Broken Root" ? new ValidationError("boom") : null;

        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Broken Root", "<p>root</p>"),
            Page("2", "1", "Orphaned Child", "<p>child</p>"),
            Page("3", null, "Healthy Sibling", "<p>sibling</p>"),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.DoesNotContain(_pageService.CreateCalls, c => c.Title == "Orphaned Child");

        var brokenRoot = result.Report.Pages.Single(p => p.Title == "Broken Root");
        Assert.Contains("page creation failed", brokenRoot.SkippedReason);

        var orphan = result.Report.Pages.Single(p => p.Title == "Orphaned Child");
        Assert.Contains("ancestor", orphan.SkippedReason, StringComparison.OrdinalIgnoreCase);

        var sibling = result.Report.Pages.Single(p => p.Title == "Healthy Sibling");
        Assert.Null(sibling.SkippedReason);
        Assert.NotNull(sibling.RocketWikiPageId);
    }

    [Fact]
    public async Task Attachment_upload_failure_is_recorded_but_does_not_block_the_page_itself()
    {
        _attachmentService.FailUploadWhen = _ => new ValidationError("storage unavailable");

        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", attachments: [Attachment("300", "diagram.png", [1, 2, 3])]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.NotNull(outcome.RocketWikiPageId);
        Assert.Null(outcome.SkippedReason);
        var failure = Assert.Single(outcome.AttachmentFailures);
        Assert.Contains("diagram.png", failure);
    }

    [Fact]
    public async Task Page_content_update_failure_still_leaves_the_page_id_set_but_flags_placeholder_content()
    {
        _pageService.FailUpdateWhen = _ => new ValidationError("boom");

        var export = new ConfluenceExportSpace("ENG", "Engineering", null, [Page("1", null, "Home", "<p>Hi</p>")]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.NotNull(outcome.RocketWikiPageId);
        Assert.Contains("placeholder content", outcome.SkippedReason);
    }

    [Fact]
    public async Task Colliding_sibling_titles_produce_disambiguated_slugs()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Notes", "<p>a</p>"),
            Page("2", null, "Notes", "<p>b</p>"),
        ]);

        await CreateImporter().ImportAsync(export, DefaultOptions());

        var slugs = _pageService.CreateCalls.Select(c => c.Slug).OrderBy(s => s).ToList();
        Assert.Equal(["notes", "notes-2"], slugs);
    }

    [Fact]
    public async Task Original_author_is_carried_into_the_report_even_though_no_shadow_user_service_exists()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>", author: new ConfluenceExportAuthor("alice@example.com", "Alice Example")),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.Equal("Alice Example <alice@example.com>", outcome.OriginalAuthor);
    }

    [Fact]
    public async Task Attachment_is_uploaded_before_the_referencing_page_body_is_converted()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home",
                """<ac:image ac:alt="diagram"><ri:attachment ri:filename="diagram.png" /></ac:image>""",
                attachments: [Attachment("300", "diagram.png", [1, 2, 3])]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.Empty(outcome.AttachmentFailures);
        var finalContent = _pageService.UpdateCalls.Last(c => c.PageId == outcome.RocketWikiPageId).Content;
        Assert.Matches(@"!\[diagram\]\(attachment://[0-9a-fA-F-]{36}\)", finalContent);
    }

    [Fact]
    public async Task A_page_whose_declared_parent_is_not_in_this_export_is_treated_as_a_root()
    {
        // "999" is not any page's ConfluencePageId in this export - e.g. its parent lives
        // in a different space, or was filtered out upstream.
        var export = new ConfluenceExportSpace("ENG", "Engineering", null, [Page("1", "999", "Orphan Root", "<p>hi</p>")]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var outcome = Assert.Single(result.Report.Pages);
        Assert.NotNull(outcome.RocketWikiPageId);
        Assert.Null(outcome.SkippedReason);
        Assert.Null(_pageService.CreatedPages.Single().ParentPageId);
    }
}
