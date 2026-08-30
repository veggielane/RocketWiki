using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Tests.Pipeline.Fakes;

namespace RocketWiki.Importer.Tests.Pipeline;

/// <summary>
/// One page that will not parse must not kill the import.
///
/// <para>Every service in the pipeline does its own <c>SaveChangesAsync</c> — there is no
/// transaction, savepoint or checkpoint spanning an import. So an exception escaping the
/// conversion loop at page 700 of 900 left 699 pages, their comments and every attachment
/// already committed, the report never written (it is produced after the loop), and a
/// re-run blocked by the space key already existing. The trigger is ordinary: any named
/// HTML entity outside the converter's 24-entry table, which is most accented characters
/// — <c>&amp;Aacute;</c>, <c>&amp;oacute;</c>, <c>&amp;frac12;</c>, <c>&amp;dagger;</c>.
/// A real Confluence space hits one of those within a few hundred pages.</para>
///
/// <para>Isolated per entity instead, the same way a failed page creation, a failed
/// attachment and a failed label already were.</para>
/// </summary>
public class ConversionFailureIsolationTests
{
    /// <summary>A named entity the converter does not know — see Conversion/Internal/HtmlEntities.cs.</summary>
    private const string UnparseableBody = "<p>Jos&eacute; and &Aacute;ngel measured &frac12; a turn.</p>";

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
        IReadOnlyList<ConfluenceExportComment>? comments = null) =>
        new(id, parentId, title, body, Author: null, CreatedAtUtc: null, [], Comments: comments);

    [Fact]
    public async Task A_page_whose_body_cannot_be_parsed_is_reported_and_the_rest_of_the_import_completes()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Fine.</p>"),
            Page("2", "1", "Broken", UnparseableBody),
            Page("3", "1", "After The Broken One", "<p>Also fine.</p>"),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.True(result.Success);

        // The page after the failure is the assertion. Before this, it was never reached:
        // the exception unwound past the loop, past the report, and out of ImportAsync.
        var after = result.Report.Pages.Single(p => p.Title == "After The Broken One");
        Assert.Null(after.SkippedReason);
        Assert.Contains("Also fine", after.ConvertedMarkdown, StringComparison.Ordinal);

        var broken = result.Report.Pages.Single(p => p.Title == "Broken");
        Assert.Contains("could not be converted", broken.SkippedReason, StringComparison.Ordinal);
        // It exists — pass 1 created it with placeholder content — and the report says so,
        // because an admin who finds the page later needs to know why it is a stub.
        Assert.NotNull(broken.RocketWikiPageId);
        Assert.Contains("placeholder", broken.SkippedReason, StringComparison.Ordinal);

        Assert.Equal(3, result.Report.Pages.Count);
        Assert.True(result.Report.NeedsReview);
    }

    [Fact]
    public async Task A_comment_whose_body_cannot_be_parsed_is_skipped_without_losing_the_page()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Fine.</p>",
            [
                new ConfluenceExportComment("10", null, UnparseableBody, null, null),
                new ConfluenceExportComment("11", null, "<p>A comment that parses.</p>", null, null),
            ]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        var home = Assert.Single(result.Report.Pages);
        Assert.Null(home.SkippedReason);

        var broken = home.Comments.Single(c => c.ConfluenceCommentId == "10");
        Assert.Contains("could not be converted", broken.SkippedReason, StringComparison.Ordinal);
        Assert.Null(broken.RocketWikiCommentId);

        // The comment behind it still lands: a thread is not all-or-nothing.
        var good = home.Comments.Single(c => c.ConfluenceCommentId == "11");
        Assert.Null(good.SkippedReason);
        Assert.NotNull(good.RocketWikiCommentId);
    }

    [Fact]
    public async Task A_reply_to_an_unparseable_comment_is_skipped_with_it()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Fine.</p>",
            [
                new ConfluenceExportComment("10", null, UnparseableBody, null, null),
                new ConfluenceExportComment("11", "10", "<p>Replying to the broken one.</p>", null, null),
            ]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        // Same rule as a comment whose creation failed: a reply hanging off a comment that
        // does not exist would be a top-level comment with no context.
        var reply = Assert.Single(result.Report.Pages).Comments.Single(c => c.ConfluenceCommentId == "11");
        Assert.NotNull(reply.SkippedReason);
        Assert.Null(reply.RocketWikiCommentId);
    }

    [Fact]
    public async Task An_unexpected_failure_mid_import_returns_the_partial_report_instead_of_throwing()
    {
        // A zip entry that will not open — the shape of every failure nobody predicted
        // (dropped connection, unreadable archive member). There is no transaction, so by
        // the time this fires the pages before it are already committed; an exception here
        // used to take the report with it and leave an operator with a half-populated
        // space and no record of what was in it.
        var poisoned = new ConfluenceExportAttachment(
            "att-1", "spec.pdf", "application/pdf",
            () => throw new IOException("the archive entry could not be read"));

        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            new ConfluenceExportPage("1", null, "Home", "<p>Fine.</p>", null, null, []),
            new ConfluenceExportPage("2", "1", "Has A Bad Attachment", "<p>Fine too.</p>", null, null, [poisoned]),
        ]);

        var result = await CreateImporter().ImportAsync(export, DefaultOptions());

        Assert.False(result.Success);
        Assert.Contains("IOException", result.BlockedReason, StringComparison.Ordinal);
        Assert.Contains("already written", result.BlockedReason, StringComparison.Ordinal);

        // The report survived, and it names what was committed before the stop — which is
        // the whole reason not to let the exception escape.
        Assert.NotNull(result.Report);
        Assert.NotEmpty(_pageService.CreatedPages);
    }

    [Fact]
    public void A_dry_run_predicts_the_same_outcome_rather_than_throwing()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Fine.</p>"),
            Page("2", "1", "Broken", UnparseableBody),
        ]);

        // design.md §16: a dry run predicts what a real run will do. It threw here too,
        // which meant the one cheap way to find these before touching the database — the
        // dry run — died on the first one and reported nothing at all.
        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(2, result.Report.Pages.Count);
        Assert.Contains("could not be converted",
            result.Report.Pages.Single(p => p.Title == "Broken").SkippedReason, StringComparison.Ordinal);
        Assert.Null(result.Report.Pages.Single(p => p.Title == "Home").SkippedReason);
        Assert.Equal(1, result.Summary.SkippedPageCount);
    }
}
