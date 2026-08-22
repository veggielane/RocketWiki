namespace RocketWiki.Importer.Tests;

/// <summary>
/// Report-focused assertions: a silent loss is the failure mode that matters most for a
/// migration tool, so these tests check the report as carefully as the Markdown output.
/// </summary>
public class ReportTests : ConverterTestBase
{
    [Fact]
    public void Fully_supported_content_produces_no_report_issues_at_all()
    {
        var xhtml = """
            <h1>Title</h1>
            <p>Some <strong>bold</strong> and <em>italic</em> text with a <code>code span</code>.</p>
            <ul><li><p>one</p></li><li><p>two</p></li></ul>
            <table><tbody><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></tbody></table>
            <ac:structured-macro ac:name="code" ac:schema-version="1">
              <ac:parameter ac:name="language">json</ac:parameter>
              <ac:plain-text-body><![CDATA[{}]]></ac:plain-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Empty(result.Report.Issues);
        Assert.False(result.Report.HasLossyIssues);
    }

    [Fact]
    public void HasLossyIssues_is_false_when_only_informational_issues_are_present()
    {
        var result = Convert("""<ac:structured-macro ac:name="toc" ac:schema-version="1" />""");

        Assert.NotEmpty(result.Report.Issues);
        Assert.All(result.Report.Issues, i => Assert.Equal(IssueSeverity.Info, i.Severity));
        Assert.False(result.Report.HasLossyIssues);
    }

    [Fact]
    public void HasLossyIssues_is_true_when_a_lossy_transform_occurs()
    {
        var result = Convert("<p><u>underlined</u></p>");

        Assert.True(result.Report.HasLossyIssues);
    }

    [Fact]
    public void Every_reported_issue_carries_a_non_empty_human_readable_message()
    {
        var xhtml = """
            <p><u>underlined</u></p>
            <ac:structured-macro ac:name="gliffy" ac:schema-version="1" />
            <table><tbody><tr><td colspan="2">merged</td></tr></tbody></table>
            """;

        var result = Convert(xhtml);

        Assert.NotEmpty(result.Report.Issues);
        Assert.All(result.Report.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
    }

    [Fact]
    public void Unresolvable_reference_never_throws_it_is_always_reported_instead()
    {
        // The converter must degrade gracefully and report, never invent syntax the editor
        // can't parse and never throw for merely-unresolvable (as opposed to malformed) input.
        var xhtml = """<p><ac:link><ri:page ri:content-title="Nonexistent" /></ac:link></p>""";

        var result = Convert(xhtml);

        Assert.NotNull(result.Markdown);
        Assert.Single(result.Report.Issues);
    }
}
