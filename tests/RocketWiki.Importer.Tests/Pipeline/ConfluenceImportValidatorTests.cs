using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Tests.Pipeline;

/// <summary>
/// ConfluenceImportValidator must behave like a side-effect-free preview of
/// ConfluenceSpaceImporter: same tree order, same slugs, same link resolution, same
/// reporting shape - see ConfluenceSpaceImporterTests for the equivalent real-import
/// assertions this mirrors.
/// </summary>
public class ConfluenceImportValidatorTests
{
    private static ConfluenceExportPage Page(
        string id, string? parentId, string title, string body, ConfluenceExportAuthor? author = null) =>
        new(id, parentId, title, body, author, CreatedAtUtc: null, []);

    [Fact]
    public void Validate_resolves_a_forward_reference_link_without_creating_anything()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", """<p>See <ac:link><ri:page ri:content-title="Details" /></ac:link>.</p>"""),
            Page("2", "1", "Details", "<p>The details.</p>"),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        var home = result.Report.Pages.Single(p => p.Title == "Home");
        var details = result.Report.Pages.Single(p => p.Title == "Details");
        Assert.Contains($"page://{details.RocketWikiPageId}", home.ConvertedMarkdown);
        Assert.False(home.ConversionReport!.HasLossyIssues);
    }

    [Fact]
    public void Validate_produces_no_skipped_pages_for_a_healthy_export()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>"),
            Page("2", "1", "Child", "<p>Hi child</p>"),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(2, result.Report.Pages.Count);
        Assert.All(result.Report.Pages, p => Assert.Null(p.SkippedReason));
        Assert.Equal(0, result.Summary.SkippedPageCount);
        Assert.Equal(2, result.Summary.PagesActuallyImported);
    }

    [Fact]
    public void Validate_reports_a_page_whose_conversion_produces_nothing()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Empty Page", """<ac:structured-macro ac:name="gallery" ac:schema-version="1" />"""),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        var outcome = Assert.Single(result.Report.Pages);
        Assert.True(outcome.ProducedEmptyContent);
        Assert.Equal(1, result.Summary.PagesWithNoConvertedContent);
    }

    [Fact]
    public void Validate_summary_counts_lossy_and_info_issues_and_unresolvable_links_across_the_whole_space()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p><u>underlined</u></p>"), // 1 lossy
            Page("2", null, "Toc Page", """<ac:structured-macro ac:name="toc" ac:schema-version="1" />"""), // 1 info
            Page("3", null, "Broken Link", """<p><ac:link><ri:page ri:content-title="Missing" /></ac:link></p>"""), // 1 unresolvable link (lossy)
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(1, result.Summary.InfoIssueCount);
        Assert.Equal(2, result.Summary.LossyIssueCount); // underline + unresolvable link
        Assert.Equal(1, result.Summary.UnresolvableLinkCount);
    }

    [Fact]
    public void Validate_counts_unsupported_macros_by_name()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "A", """<ac:structured-macro ac:name="jira" ac:schema-version="1"><ac:rich-text-body><p>x</p></ac:rich-text-body></ac:structured-macro>"""),
            Page("2", null, "B", """<ac:structured-macro ac:name="jira" ac:schema-version="1"><ac:rich-text-body><p>y</p></ac:rich-text-body></ac:structured-macro>"""),
            Page("3", null, "C", """<ac:structured-macro ac:name="gliffy" ac:schema-version="1"><ac:rich-text-body><p>z</p></ac:rich-text-body></ac:structured-macro>"""),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(2, result.Summary.UnsupportedMacroCounts["jira"]);
        Assert.Equal(1, result.Summary.UnsupportedMacroCounts["gliffy"]);
    }

    [Fact]
    public void Validate_reports_orphaned_pages_from_a_parent_cycle_and_excludes_them_from_the_imported_count()
    {
        // "1" claims "2" as parent and "2" claims "1" as parent - neither is reachable
        // from a root, so ImportTreePlanner marks both orphaned.
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", "2", "A", "<p>a</p>"),
            Page("2", "1", "B", "<p>b</p>"),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(2, result.Report.Pages.Count);
        Assert.All(result.Report.Pages, p => Assert.NotNull(p.SkippedReason));
        Assert.Equal(2, result.Summary.SkippedPageCount);
        Assert.Equal(0, result.Summary.PagesActuallyImported);
    }

    [Fact]
    public void Validate_disambiguates_colliding_sibling_slugs_the_same_way_the_real_importer_would()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Notes", "<p>a</p>"),
            Page("2", null, "Notes", "<p>b</p>"),
        ]);

        // The validator doesn't expose slugs directly (it never creates anything with
        // one), but it must not treat the collision as a failure - both pages import.
        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Equal(2, result.Summary.PagesActuallyImported);
        Assert.All(result.Report.Pages, p => Assert.NotNull(p.RocketWikiPageId));
    }

    [Fact]
    public void Validate_never_mutates_or_depends_on_anything_outside_the_export_and_can_run_twice_independently()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null, [Page("1", null, "Home", "<p>Hi</p>")]);
        var validator = new ConfluenceImportValidator();

        var first = validator.Validate(export);
        var second = validator.Validate(export);

        Assert.Equal(first.Summary.PagesActuallyImported, second.Summary.PagesActuallyImported);
        Assert.NotEqual(first.Report.Pages[0].RocketWikiPageId, second.Report.Pages[0].RocketWikiPageId); // fresh ids each run, by design
    }
}
