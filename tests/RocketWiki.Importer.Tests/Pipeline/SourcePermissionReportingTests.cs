using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Reporting;

namespace RocketWiki.Importer.Tests.Pipeline;

/// <summary>
/// design.md §13's second half. The importer takes a required initial grant and applies
/// nothing the source had — which is only the safe choice if the operator is told what
/// the source had. Before this existed, a Confluence page restricted to five people
/// landed in a space every holder of the grant expression could read, and no artefact
/// anywhere recorded that a restriction had ever been there: the report said "no issues".
///
/// These assert the reporting path end to end (export → report → the text a person
/// actually reads), because a field populated on a record nobody prints is the same
/// silence with more code.
/// </summary>
public class SourcePermissionReportingTests
{
    private static ConfluenceExportPage Page(
        string id, string? parentId, string title, string body,
        IReadOnlyList<ConfluenceExportPermission>? restrictions = null) =>
        new(id, parentId, title, body, Author: null, CreatedAtUtc: null, [], Restrictions: restrictions);

    private static string FormatDryRun(ConfluenceExportSpace export)
    {
        var result = new ConfluenceImportValidator().Validate(export);
        return ImportReportTextFormatter.Format(export.Key, isDryRun: true, result.Report, result.Summary);
    }

    [Fact]
    public void A_page_restricted_in_confluence_is_never_reported_as_clean()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
        [
            Page("1", null, "Home", "<p>Hi</p>"),
            Page("2", "1", "Nozzle Geometry", "<p>Numbers.</p>",
                [new ConfluenceExportPermission("View", "group", "itar-cleared")]),
        ]);

        var result = new ConfluenceImportValidator().Validate(export);

        // Nothing failed and nothing converted badly, so every other signal in this
        // report says "fine". This one must not.
        Assert.True(result.Report.NeedsReview);
        Assert.Equal(1, result.Summary.SourcePermissionCount);

        var text = ImportReportTextFormatter.Format("ENG", isDryRun: true, result.Report, result.Summary);
        Assert.Contains("NONE OF THESE WERE APPLIED", text, StringComparison.Ordinal);
        Assert.Contains("itar-cleared", text, StringComparison.Ordinal);
        Assert.Contains("Nozzle Geometry", text, StringComparison.Ordinal);

        // And it is filed under "needs review", not in the quiet list at the end.
        var reviewIndex = text.IndexOf("== Pages needing review", StringComparison.Ordinal);
        var cleanIndex = text.IndexOf("== Pages with no issues", StringComparison.Ordinal);
        var pageIndex = text.IndexOf("--- Nozzle Geometry", StringComparison.Ordinal);
        Assert.True(pageIndex > reviewIndex);
        Assert.True(cleanIndex < 0 || pageIndex < cleanIndex);
    }

    [Fact]
    public void Space_permissions_are_reported_even_though_the_import_applies_none_of_them()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
            [Page("1", null, "Home", "<p>Hi</p>")],
            [
                new ConfluenceExportPermission("VIEWSPACE", "group", "propulsion-engineers"),
                new ConfluenceExportPermission("VIEWSPACE", "anonymous", null),
            ]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.True(result.Report.NeedsReview);
        Assert.Equal(2, result.Summary.SourceSpacePermissionCount);

        var text = ImportReportTextFormatter.Format("ENG", isDryRun: true, result.Report, result.Summary);

        // Grouped by Confluence permission type — the unit an admin actually re-applies —
        // and every subject labelled as foreign, so "propulsion-engineers" is never read
        // as a group that exists on this instance.
        Assert.Contains("VIEWSPACE (2):", text, StringComparison.Ordinal);
        Assert.Contains("Confluence group 'propulsion-engineers'", text, StringComparison.Ordinal);

        // The line worth shouting about: RocketWiki has no anonymous access to re-apply to.
        Assert.Contains("ANONYMOUS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_with_no_permissions_says_so_rather_than_saying_nothing()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null, [Page("1", null, "Home", "<p>Hi</p>")]);

        var result = new ConfluenceImportValidator().Validate(export);
        var text = ImportReportTextFormatter.Format("ENG", isDryRun: true, result.Report, result.Summary);

        // "The export carried none" and "nobody looked" have to read differently. They
        // did not before: the section did not exist, so every report looked like this one.
        Assert.Contains("The export carried no space permissions and no page restrictions.", text, StringComparison.Ordinal);
        Assert.False(result.Report.NeedsReview);
    }

    [Fact]
    public void The_report_names_the_permission_type_verbatim_even_when_it_means_nothing_here()
    {
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
            [Page("1", null, "Home", "<p>Hi</p>")],
            [new ConfluenceExportPermission("SOMEFUTUREPERMISSION", "group", "confluence-administrators")]);

        var text = FormatDryRun(export);

        // Translating is what §13 forbids; summarising an unknown type as "other" is
        // translation with the interesting part removed.
        Assert.Contains("SOMEFUTUREPERMISSION", text, StringComparison.Ordinal);
    }
}
