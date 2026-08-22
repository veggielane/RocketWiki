namespace RocketWiki.Importer.Tests;

public class MacroTests : ConverterTestBase
{
    [Fact]
    public void Code_macro_with_language_becomes_a_fenced_block_preserving_the_language()
    {
        var xhtml = """
            <ac:structured-macro ac:name="code" ac:schema-version="1">
              <ac:parameter ac:name="language">csharp</ac:parameter>
              <ac:plain-text-body><![CDATA[public class Foo {}]]></ac:plain-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal("```csharp\npublic class Foo {}\n```\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Code_macro_without_a_language_parameter_becomes_a_bare_fenced_block()
    {
        var xhtml = """
            <ac:structured-macro ac:name="code" ac:schema-version="1">
              <ac:plain-text-body><![CDATA[echo hello]]></ac:plain-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal("```\necho hello\n```\n", result.Markdown);
    }

    [Fact]
    public void Code_macro_body_containing_backticks_widens_the_fence()
    {
        var xhtml = """
            <ac:structured-macro ac:name="code" ac:schema-version="1">
              <ac:parameter ac:name="language">markdown</ac:parameter>
              <ac:plain-text-body><![CDATA[Use ``` for code blocks]]></ac:plain-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal("````markdown\nUse ``` for code blocks\n````\n", result.Markdown);
    }

    [Theory]
    [InlineData("info", "info")]
    [InlineData("note", "note")]
    [InlineData("warning", "warning")]
    public void Panel_macros_convert_to_their_matching_callout_directive(string macroName, string directive)
    {
        var xhtml = $"""
            <ac:structured-macro ac:name="{macroName}" ac:schema-version="1">
              <ac:rich-text-body><p>Heads up.</p></ac:rich-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal($":::{directive}\nHeads up.\n:::\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Tip_macro_maps_to_info_directive_and_is_reported_as_a_remapping()
    {
        var xhtml = """
            <ac:structured-macro ac:name="tip" ac:schema-version="1">
              <ac:rich-text-body><p>Handy trick.</p></ac:rich-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal(":::info\nHandy trick.\n:::\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Contains("tip", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("info", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Panel_title_is_flattened_into_the_callout_body_and_reported_as_lossy()
    {
        var xhtml = """
            <ac:structured-macro ac:name="warning" ac:schema-version="1">
              <ac:parameter ac:name="title">Danger zone</ac:parameter>
              <ac:rich-text-body><p>Proceed carefully.</p></ac:rich-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal(":::warning\n**Danger zone**\n\nProceed carefully.\n:::\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Contains("title", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Toc_macro_is_dropped_and_reported_as_informational()
    {
        var result = Convert("<h1>Title</h1><ac:structured-macro ac:name=\"toc\" ac:schema-version=\"1\" />");

        Assert.Equal("# Title\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Equal(IssueCategory.DroppedElement, issue.Category);
    }

    [Fact]
    public void Status_macro_becomes_inline_code_and_colour_loss_is_reported()
    {
        var xhtml = """
            <p>Ticket state:
            <ac:structured-macro ac:name="status" ac:schema-version="1">
              <ac:parameter ac:name="colour">Green</ac:parameter>
              <ac:parameter ac:name="title">DONE</ac:parameter>
            </ac:structured-macro>
            </p>
            """;

        var result = Convert(xhtml);

        Assert.Equal("Ticket state: `DONE`\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Contains("colour", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expand_macro_flattens_to_static_content_with_title_preserved_and_is_reported_lossy()
    {
        var xhtml = """
            <ac:structured-macro ac:name="expand" ac:schema-version="1">
              <ac:parameter ac:name="title">Click to expand</ac:parameter>
              <ac:rich-text-body><p>Hidden details.</p></ac:rich-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal("**Click to expand**\n\nHidden details.\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Contains("collapsible", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_macro_with_rich_text_body_preserves_its_text_and_is_reported()
    {
        var xhtml = """
            <ac:structured-macro ac:name="jira" ac:schema-version="1">
              <ac:rich-text-body><p>PROJ-123: Fix the widget</p></ac:rich-text-body>
            </ac:structured-macro>
            """;

        var result = Convert(xhtml);

        Assert.Equal("PROJ-123: Fix the widget\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.UnsupportedMacro, issue.Category);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Contains("jira", issue.Message);
    }

    [Fact]
    public void Unknown_macro_with_no_extractable_content_is_dropped_entirely_and_reported()
    {
        var xhtml = """<ac:structured-macro ac:name="gallery" ac:schema-version="1" />""";

        var result = Convert(xhtml);

        Assert.Equal(string.Empty, result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.UnsupportedMacro, issue.Category);
        Assert.Contains("gallery", issue.Message);
        Assert.Contains("dropped entirely", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_layout_columns_are_flattened_into_sequential_content_and_reported()
    {
        var xhtml = """
            <ac:layout>
              <ac:layout-section ac:type="two_equal">
                <ac:layout-cell><p>Left column</p></ac:layout-cell>
                <ac:layout-cell><p>Right column</p></ac:layout-cell>
              </ac:layout-section>
            </ac:layout>
            """;

        var result = Convert(xhtml);

        Assert.Equal("Left column\n\nRight column\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Contains("column", issue.Message, StringComparison.OrdinalIgnoreCase);
    }
}
