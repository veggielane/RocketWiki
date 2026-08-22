namespace RocketWiki.Importer.Tests;

public class LinkAndAttachmentTests : ConverterTestBase
{
    [Fact]
    public void Resolvable_page_link_converts_to_page_scheme_url()
    {
        Resolver.WithPage("Other Page", "page-123");
        var xhtml = """
            <p><ac:link><ri:page ri:content-title="Other Page" ri:space-key="ENG" />
              <ac:plain-text-link-body><![CDATA[See other page]]></ac:plain-text-link-body>
            </ac:link></p>
            """;

        var result = Convert(xhtml);

        Assert.Equal("[See other page](page://page-123)\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Page_link_without_custom_text_falls_back_to_the_page_title()
    {
        Resolver.WithPage("Other Page", "page-123");
        var xhtml = """<p><ac:link><ri:page ri:content-title="Other Page" ri:space-key="ENG" /></ac:link></p>""";

        var result = Convert(xhtml);

        Assert.Equal("[Other Page](page://page-123)\n", result.Markdown);
    }

    [Fact]
    public void Unresolvable_page_link_flattens_to_plain_text_and_is_reported()
    {
        var xhtml = """
            <p><ac:link><ri:page ri:content-title="Missing Page" ri:space-key="ENG" />
              <ac:plain-text-link-body><![CDATA[broken link]]></ac:plain-text-link-body>
            </ac:link></p>
            """;

        var result = Convert(xhtml);

        Assert.Equal("broken link\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Equal(IssueCategory.UnresolvedLink, issue.Category);
        Assert.Contains("Missing Page", issue.Message);
    }

    [Fact]
    public void Attachment_image_converts_to_attachment_scheme_image()
    {
        Resolver.WithAttachment("diagram.png", "attach-1");
        var xhtml = """<ac:image ac:alt="Architecture diagram"><ri:attachment ri:filename="diagram.png" /></ac:image>""";

        var result = Convert(xhtml);

        Assert.Equal("![Architecture diagram](attachment://attach-1)\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Unresolvable_attachment_image_is_dropped_and_reported()
    {
        var xhtml = """<ac:image ac:alt="Missing"><ri:attachment ri:filename="missing.png" /></ac:image>""";

        var result = Convert(xhtml);

        Assert.Equal(string.Empty, result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.UnresolvedLink, issue.Category);
        Assert.Contains("missing.png", issue.Message);
    }

    [Fact]
    public void Attachment_link_to_a_pdf_converts_to_attachment_scheme_link()
    {
        Resolver.WithAttachment("spec.pdf", "attach-2");
        var xhtml = """
            <p><ac:link><ri:attachment ri:filename="spec.pdf" />
              <ac:plain-text-link-body><![CDATA[Spec PDF]]></ac:plain-text-link-body>
            </ac:link></p>
            """;

        var result = Convert(xhtml);

        Assert.Equal("[Spec PDF](attachment://attach-2)\n", result.Markdown);
    }

    [Fact]
    public void Attachment_belonging_to_another_page_resolves_using_the_nested_ri_page_context()
    {
        Resolver.WithAttachment("shared.png", "attach-shared");
        var xhtml = """
            <ac:image>
              <ri:attachment ri:filename="shared.png">
                <ri:page ri:content-title="Other Page" ri:space-key="ENG" />
              </ri:attachment>
            </ac:image>
            """;

        var result = Convert(xhtml);

        Assert.Equal("![shared.png](attachment://attach-shared)\n", result.Markdown);
    }

    [Fact]
    public void External_image_via_ri_url_is_passed_through_and_reported_informationally()
    {
        var xhtml = """<ac:image ac:alt="logo"><ri:url ri:value="https://example.com/logo.png" /></ac:image>""";

        var result = Convert(xhtml);

        Assert.Equal("![logo](https://example.com/logo.png)\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
    }

    [Fact]
    public void User_mention_link_flattens_to_plain_text_and_is_reported()
    {
        var xhtml = """
            <p><ac:link><ri:user ri:userkey="8a7f...deadbeef" />
              <ac:plain-text-link-body><![CDATA[Jane Doe]]></ac:plain-text-link-body>
            </ac:link></p>
            """;

        var result = Convert(xhtml);

        Assert.Equal("Jane Doe\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.UnresolvedLink, issue.Category);
    }

    [Fact]
    public void Page_link_defaults_to_the_current_page_space_when_space_key_is_omitted()
    {
        Resolver.WithPage("Sibling Page", "page-456");
        var xhtml = """<p><ac:link><ri:page ri:content-title="Sibling Page" /></ac:link></p>""";

        var result = Convert(xhtml);

        Assert.Equal("[Sibling Page](page://page-456)\n", result.Markdown);
    }
}
