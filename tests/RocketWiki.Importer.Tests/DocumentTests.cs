namespace RocketWiki.Importer.Tests;

/// <summary>
/// A full, realistic page combining headings, formatting, a list, a table, a macro, and a
/// link in one document — proving the block/inline renderers compose correctly, not just
/// each feature in isolation.
/// </summary>
public class DocumentTests : ConverterTestBase
{
    [Fact]
    public void Composite_page_converts_every_section_correctly_in_one_pass()
    {
        Resolver.WithPage("Runbook", "page-runbook");

        var xhtml = """
            <h1>Deployment Guide</h1>
            <p>This page covers the <strong>production</strong> rollout. See the <ac:link><ri:page ri:content-title="Runbook" /></ac:link> for incident response.</p>
            <h2>Steps</h2>
            <ol>
              <li><p>Build the release artifact.</p></li>
              <li><p>Run the smoke tests.</p></li>
            </ol>
            <ac:structured-macro ac:name="warning" ac:schema-version="1">
              <ac:rich-text-body><p>Do not deploy on a Friday.</p></ac:rich-text-body>
            </ac:structured-macro>
            <h2>Rollback matrix</h2>
            <table>
              <tbody>
                <tr><th>Environment</th><th>Command</th></tr>
                <tr><td>staging</td><td><code>deploy rollback staging</code></td></tr>
                <tr><td>production</td><td><code>deploy rollback prod</code></td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        var expected = "# Deployment Guide\n\n" +
            "This page covers the **production** rollout. See the [Runbook](page://page-runbook) for incident response.\n\n" +
            "## Steps\n\n" +
            "1. Build the release artifact.\n2. Run the smoke tests.\n\n" +
            ":::warning\nDo not deploy on a Friday.\n:::\n\n" +
            "## Rollback matrix\n\n" +
            "| Environment | Command |\n| --- | --- |\n| staging | `deploy rollback staging` |\n| production | `deploy rollback prod` |\n";

        Assert.Equal(expected, result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Composite_page_with_several_lossy_constructs_reports_every_one()
    {
        var xhtml = """
            <h1>Legacy Page</h1>
            <p><u>Important:</u> read this first.</p>
            <table>
              <tbody>
                <tr><td colspan="2">Header-less merged table</td></tr>
                <tr><td>a</td><td>b</td></tr>
              </tbody>
            </table>
            <ac:structured-macro ac:name="widget" ac:schema-version="1">
              <ac:plain-text-body><![CDATA[fallback text]]></ac:plain-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        // Underline, no-header-row, and unsupported-macro should each be reported. The
        // merged cell is NOT an issue any more — colspans convert faithfully to the §4
        // adjacent-pipe syntax (see TableTests), so reporting one would be a false alarm.
        Assert.Equal(3, result.Report.Issues.Count);
        Assert.Contains(result.Report.Issues, i => i.Message.Contains("Underline"));
        Assert.Contains(result.Report.Issues, i => i.Message.Contains("no header row"));
        Assert.Contains(result.Report.Issues, i => i.Category == IssueCategory.UnsupportedMacro);
        Assert.DoesNotContain(result.Report.Issues, i => i.Message.Contains("merged", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("| Header-less merged table ||\n", result.Markdown);
        Assert.True(result.Report.HasLossyIssues);
    }
}
