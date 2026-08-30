using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Tests;

/// <summary>
/// The converter's input is a Confluence page authored by somebody outside this system —
/// on a migration, usually outside this organisation. These are the places where a value
/// from that source was interpolated into Markdown without being made safe to interpolate.
///
/// <para>None of them is XSS: the SPA feeds ProseMirror JSON (never HTML) into the editor,
/// <c>markdown-it</c> runs with <c>html:false</c>, the link mark blanks
/// <c>javascript:</c>/<c>data:</c> hrefs, and the image node renders only
/// <c>attachment://</c> sources. That is exactly why they are fixed HERE as well: a
/// converter whose output is safe only because of what the renderer happens to do has made
/// the renderer's behaviour part of its contract without saying so, and stored content
/// should not contain <c>[click](javascript:…)</c> in the first place.</para>
/// </summary>
public class ConverterHostileSourceTests : ConverterTestBase
{
    /// <summary>
    /// The sharp one. A code macro's <c>language</c> parameter was interpolated straight
    /// after the opening fence, so a newline in it closed the info string and everything
    /// after became live Markdown blocks — headings, lists, tables, links — inside what an
    /// operator sees as a code block. An info string has no escaping mechanism, so the
    /// characters are dropped rather than escaped.
    ///
    /// <para>Mutation-tested: restore the raw <c>{language}</c> interpolation and this
    /// fails, with the injected heading appearing as a heading.</para>
    /// </summary>
    [Fact]
    public void Code_macro_language_cannot_break_out_of_the_fence_info_string()
    {
        var result = Convert("""
            <ac:structured-macro ac:name="code">
              <ac:parameter ac:name="language">text
            # Injected heading

            Escaped prose.</ac:parameter>
              <ac:plain-text-body><![CDATA[real code]]></ac:plain-text-body>
            </ac:structured-macro>
            """);

        // The fence's opening line stays ONE line: whatever survived sanitizing is an info
        // string, not a heading and not a blank line that ends the block. (`#` itself is a
        // legal info-string character - `c#` is a language - so what makes the injection
        // harmless is that its line breaks are gone, not that its hashes are.)
        var lines = result.Markdown.Split('\n');
        Assert.StartsWith("```", lines[0], StringComparison.Ordinal);
        Assert.True(lines[0].Length <= 3 + MarkdownText.MaxFenceLanguageLength, $"fence line is '{lines[0]}'");

        // Nothing from the parameter became a block of its own.
        Assert.DoesNotContain("# Injected heading", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.StartsWith("# ", StringComparison.Ordinal));
        Assert.Contains("real code", result.Markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("java script")] // a space
    [InlineData("c++;rm -rf")] // a semicolon
    [InlineData("`+```")] // backticks, which would confuse the fence itself
    public void Code_macro_language_keeps_only_language_tag_characters(string declared)
    {
        var result = Convert($"""
            <ac:structured-macro ac:name="code">
              <ac:parameter ac:name="language">{declared}</ac:parameter>
              <ac:plain-text-body><![CDATA[body]]></ac:plain-text-body>
            </ac:structured-macro>
            """);

        var firstLine = result.Markdown.Split('\n')[0];
        var info = firstLine.TrimStart('`');
        Assert.All(info, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.' or '#' or '_',
            $"fence info string still carries '{c}'"));
    }

    /// <summary>An ordinary language is untouched — the sanitizer must not be a
    /// regression in what it allows through.</summary>
    [Fact]
    public void Code_macro_language_that_is_ordinary_survives_intact()
    {
        var result = Convert("""
            <ac:structured-macro ac:name="code">
              <ac:parameter ac:name="language">c#</ac:parameter>
              <ac:plain-text-body><![CDATA[var x = 1;]]></ac:plain-text-body>
            </ac:structured-macro>
            """);

        Assert.StartsWith("```c#", result.Markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>EscapeLinkUrl</c> is breakout-safe but scheme-blind, so a
    /// <c>javascript:</c> href landed in stored content intact.
    ///
    /// <para>Mutation-tested: remove the <c>IsAllowedLinkUrl</c> guard from RenderAnchor
    /// and this fails with the scheme present in the output.</para>
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox")]
    [InlineData("file:///etc/passwd")]
    public void Anchor_with_a_disallowed_scheme_is_flattened_to_its_text(string href)
    {
        var result = Convert($"""<p><a href="{href}">Click me</a></p>""");

        Assert.Contains("Click me", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("](", result.Markdown, StringComparison.Ordinal);
        Assert.Contains(result.Report.Issues, i => i.Message.Contains("URL scheme", StringComparison.Ordinal));
    }

    /// <summary>A control character between the scheme letters is the classic way past a
    /// naive prefix check; browsers strip it before parsing the URL.</summary>
    [Fact]
    public void Anchor_with_a_scheme_split_by_a_control_character_is_flattened()
    {
        var result = Convert("<p><a href=\"java&#9;script:alert(1)\">Click me</a></p>");

        Assert.DoesNotContain("](", result.Markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://intranet.example/runbook")]
    [InlineData("http://intranet.example/runbook")]
    [InlineData("mailto:ops@example.com")]
    [InlineData("/relative/path")]
    [InlineData("#anchor")]
    [InlineData("../sibling/page")]
    public void Anchor_with_an_ordinary_url_still_becomes_a_link(string href)
    {
        var result = Convert($"""<p><a href="{href}">Runbook</a></p>""");

        Assert.Contains($"[Runbook]({href})", result.Markdown, StringComparison.Ordinal);
    }

    /// <summary>The same allowlist on the two image paths: a raw <c>&lt;img&gt;</c> and an
    /// <c>ac:image</c> with an <c>ri:url</c>. A <c>data:</c> image source is how a payload
    /// rides in a src attribute.</summary>
    [Fact]
    public void Image_with_a_disallowed_scheme_is_dropped_to_its_alt_text()
    {
        var raw = Convert("""<p><img src="data:image/svg+xml,&lt;svg onload=alert(1)&gt;" alt="Diagram" /></p>""");
        Assert.Equal("Diagram", raw.Markdown.Trim());
        Assert.DoesNotContain("![", raw.Markdown, StringComparison.Ordinal);

        var acImage = Convert("""
            <p><ac:image ac:alt="Diagram"><ri:url ri:value="javascript:alert(1)" /></ac:image></p>
            """);
        Assert.Equal("Diagram", acImage.Markdown.Trim());
        Assert.DoesNotContain("![", acImage.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_with_an_ordinary_url_still_renders()
    {
        var result = Convert("""<p><img src="https://intranet.example/plan.png" alt="Plan" /></p>""");

        Assert.Contains("![Plan](https://intranet.example/plan.png)", result.Markdown, StringComparison.Ordinal);
    }
}
