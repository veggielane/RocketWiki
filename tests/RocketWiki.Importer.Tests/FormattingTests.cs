namespace RocketWiki.Importer.Tests;

public class FormattingTests : ConverterTestBase
{
    [Fact]
    public void Heading_and_paragraph_convert_to_gfm()
    {
        var result = Convert("<h1>Title</h1><p>Hello world.</p>");

        Assert.Equal("# Title\n\nHello world.\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Bold_italic_and_strike_use_canonical_markers()
    {
        var result = Convert("<p><strong>bold</strong> <em>italic</em> <s>struck</s></p>");

        Assert.Equal("**bold** *italic* ~~struck~~\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void B_and_i_tags_render_the_same_as_strong_and_em()
    {
        var result = Convert("<p><b>bold</b> <i>italic</i></p>");

        Assert.Equal("**bold** *italic*\n", result.Markdown);
    }

    [Fact]
    public void Inline_code_is_wrapped_in_backticks()
    {
        var result = Convert("<p>Run <code>dotnet build</code> first.</p>");

        Assert.Equal("Run `dotnet build` first.\n", result.Markdown);
    }

    [Fact]
    public void Underline_is_stripped_and_reported_as_lossy()
    {
        var result = Convert("<p><u>underlined</u> text</p>");

        Assert.Equal("underlined text\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Equal(IssueCategory.LossyTransform, issue.Category);
        Assert.Contains("Underline", issue.Message);
    }

    [Fact]
    public void Blockquote_prefixes_every_line_including_blank_ones_between_paragraphs()
    {
        var result = Convert("<blockquote><p>First para.</p><p>Second para.</p></blockquote>");

        Assert.Equal("> First para.\n>\n> Second para.\n", result.Markdown);
    }

    [Fact]
    public void External_link_converts_to_gfm_link()
    {
        var result = Convert("<p>See <a href=\"https://example.com/docs\">the docs</a>.</p>");

        Assert.Equal("See [the docs](https://example.com/docs).\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Literal_markdown_special_characters_in_text_are_escaped()
    {
        var result = Convert("<p>Use *bold* and _italic_ markers, plus [brackets].</p>");

        Assert.Equal("Use \\*bold\\* and \\_italic\\_ markers, plus \\[brackets\\].\n", result.Markdown);
    }

    [Fact]
    public void Paragraph_text_starting_with_hash_is_escaped_so_it_is_not_parsed_as_a_heading()
    {
        var result = Convert("<p># not a heading</p>");

        Assert.Equal("\\# not a heading\n", result.Markdown);
    }

    [Fact]
    public void Horizontal_rule_converts_to_gfm_thematic_break()
    {
        var result = Convert("<p>Before</p><hr/><p>After</p>");

        Assert.Equal("Before\n\n---\n\nAfter\n", result.Markdown);
    }

    [Fact]
    public void Preformatted_block_without_a_macro_becomes_a_fenced_code_block()
    {
        var result = Convert("<pre>plain\npreformatted text</pre>");

        Assert.Equal("```\nplain\npreformatted text\n```\n", result.Markdown);
    }

    [Fact]
    public void Unrecognized_inline_element_is_unwrapped_and_reported()
    {
        var result = Convert("<p>Some <tt>teletype</tt> text.</p>");

        Assert.Equal("Some teletype text.\n", result.Markdown);
        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.DroppedElement, issue.Category);
        Assert.Contains("tt", issue.Message);
    }

    [Fact]
    public void Unrecognized_block_element_with_no_text_is_dropped_and_reported()
    {
        var result = Convert("<p>Before</p><some-widget/><p>After</p>");

        Assert.Equal("Before\n\nAfter\n", result.Markdown);
        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.DroppedElement, issue.Category);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
    }

    [Fact]
    public void Named_html_entities_from_real_confluence_exports_decode_correctly()
    {
        var result = Convert("<p>Copyright &copy; 2026 &mdash; all rights reserved&hellip;</p>");

        Assert.Equal("Copyright © 2026 — all rights reserved…\n", result.Markdown);
    }

    [Fact]
    public void Heading_path_is_used_as_the_report_location_for_issues_within_a_section()
    {
        var result = Convert("<h1>Setup</h1><h2>Prerequisites</h2><p><u>required</u> tools</p>");

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal("Setup > Prerequisites", issue.Location);
    }
}
