using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Reporting;
using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Tests;

/// <summary>
/// Four places the converter emitted Markdown that does not parse as what it means, all
/// of the same shape: a delimiter chosen without looking at the content it has to
/// contain, or an escape applied on one path and forgotten on its siblings. None of them
/// throws — the page imports, and the damage is only visible when someone reads it.
/// </summary>
public class ConverterEscapingTests : ConverterTestBase
{
    [Fact]
    public void Inline_code_containing_backticks_widens_its_delimiter()
    {
        // Documentation about Markdown, shell snippets, Confluence status macros: all
        // ordinary wiki content, all carrying backticks. A single-backtick wrapper turned
        // one code span into three fragments. The fenced-BLOCK path already widened;
        // inline did not, and nothing tested it.
        var result = Convert("<p>Run <code>use `git log` here</code> first.</p>");

        Assert.Contains("``use `git log` here``", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_starting_or_ending_with_a_backtick_is_padded()
    {
        // CommonMark strips one leading and one trailing space inside a code span, so
        // padding is how content that begins with a backtick stays parseable — without
        // it the delimiter and the content run together.
        var result = Convert("<p><code>`quoted`</code></p>");

        Assert.Contains("`` `quoted` ``", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_urls_are_escaped_the_same_way_link_urls_are()
    {
        // The anchor path escaped; both image paths did not. A URL with a space is the
        // single most common shape here (SharePoint, Jira, anything pasted from a
        // browser), so the unescaped paths were the ones that mattered.
        var result = Convert("""<p><img src="https://intranet.example/My Docs/plan (final).png" alt="Plan" /></p>""");

        Assert.Contains("%20", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("%28", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("plan (final)", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_callouts_use_a_wider_outer_fence()
    {
        // An info panel containing a note panel is ordinary Confluence. With a fixed
        // ":::" the first closing fence terminated the OUTER callout, so the inner
        // panel's content and everything after it escaped the container.
        var result = Convert("""
            <ac:structured-macro ac:name="info">
              <ac:rich-text-body>
                <p>Outer.</p>
                <ac:structured-macro ac:name="note">
                  <ac:rich-text-body><p>Inner.</p></ac:rich-text-body>
                </ac:structured-macro>
              </ac:rich-text-body>
            </ac:structured-macro>
            """);

        var lines = result.Markdown.Split('\n');
        var opening = lines.First(l => l.StartsWith("::", StringComparison.Ordinal));
        var innerOpening = lines.First(l => l.TrimStart().StartsWith(":::note", StringComparison.Ordinal));

        Assert.StartsWith("::::info", opening, StringComparison.Ordinal);
        Assert.Equal(":::note", innerOpening.Trim());

        // And the outer fence closes after the inner one, which is the property the bug
        // actually broke.
        var lastFence = lines.Last(l => l.StartsWith("::::", StringComparison.Ordinal));
        Assert.Equal("::::", lastFence.Trim());
    }

    [Fact]
    public void A_link_to_a_section_of_another_page_reports_the_dropped_anchor()
    {
        // §13 step 4 requires a per-page report of anything lossy. A link to a SECTION
        // silently became a whole-page link — and the rarer form (an anchor with no ri:
        // child) did report, so the common one was the quiet failure.
        var result = Convert("""
            <p><ac:link ac:anchor="Prerequisites"><ri:page ri:content-title="Setup" /></ac:link></p>
            """);

        var issue = Assert.Single(result.Report.Issues, i => i.Message.Contains("section", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Equal("Prerequisites", issue.Detail);
    }

    [Fact]
    public void The_report_prints_the_diagnostic_detail_the_converter_collected()
    {
        // Detail is documented as "a source snippet, macro name, or URL — for triage"
        // and was populated at every issue site, then dropped at print time. "Unrecognized
        // element was dropped" without the snippet sends the reviewer back to the export
        // to find it by hand.
        var result = Convert("<p><dl><dt>Term</dt><dd>Definition</dd></dl></p>");

        var withDetail = result.Report.Issues.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Detail));
        Assert.NotNull(withDetail);

        var report = new ImportReport();
        report.AddPage(new PageImportOutcome(
            "1", "Page", Guid.NewGuid(), result.Report, [], null, null));
        var text = ImportReportTextFormatter.Format(
            "ENG", isDryRun: true, report, new ImportValidationSummary(1, 0, 0, 0, 0, 0, new Dictionary<string, int>()));

        Assert.Contains("detail:", text, StringComparison.Ordinal);
        Assert.Contains(withDetail!.Detail!.Split('\n')[0][..10], text, StringComparison.Ordinal);
    }
    [Fact]
    public void A_code_macro_declaring_a_reserved_language_does_not_produce_that_fence()
    {
        // design.md §4 gives a handful of fence languages a MEANING rather than a
        // highlighting hint — the SPA decodes their bodies. A Confluence author can type
        // anything into a code macro's language parameter, and it went into the fence
        // unvalidated, so a block tagged "mermaid" arrived as a diagram source and one
        // tagged "drawio" as base64 of an SVG. Dropped to an untagged fence instead:
        // unhighlighted content is the harmless direction.
        var result = Convert("""
            <ac:structured-macro ac:name="code">
              <ac:parameter ac:name="language">mermaid</ac:parameter>
              <ac:plain-text-body><![CDATA[not actually a diagram]]></ac:plain-text-body>
            </ac:structured-macro>
            """);

        Assert.DoesNotContain("```mermaid", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("not actually a diagram", result.Markdown, StringComparison.Ordinal);
        Assert.Contains(result.Report.Issues, i => i.Detail == "mermaid");
    }

    [Fact]
    public void An_ordinary_code_language_still_reaches_the_fence()
    {
        // The non-vacuity half: the guard must not strip every language.
        var result = Convert("""
            <ac:structured-macro ac:name="code">
              <ac:parameter ac:name="language">python</ac:parameter>
              <ac:plain-text-body><![CDATA[print("hi")]]></ac:plain-text-body>
            </ac:structured-macro>
            """);

        Assert.Contains("```python", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordered_list_honours_its_start_attribute()
    {
        // A procedure split by a note or a screenshot is ordinary migrated content, and
        // renumbering from 1 turns step 7 into step 1 silently.
        var result = Convert("<ol start=\"7\"><li>Seven</li><li>Eight</li></ol>");

        Assert.Contains("7. Seven", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("8. Eight", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_issue_raised_inside_a_heading_is_filed_under_that_heading()
    {
        // Locations come from state.CurrentLocation, so rendering the heading's inline
        // content before entering the section filed its issues under the PREVIOUS
        // section's breadcrumb — pointing the reviewer at the wrong part of the page.
        var result = Convert("""
            <h2>First Section</h2>
            <p>Body.</p>
            <h2>Second <ac:structured-macro ac:name="unknown-macro" /> Section</h2>
            """);

        var issue = Assert.Single(result.Report.Issues, i => i.Category == IssueCategory.UnsupportedMacro);

        // The breadcrumb names the heading the macro is IN. Before, the section was
        // entered only after its own inline content had been rendered, so this issue
        // was located at "First Section" — the previous one.
        Assert.Contains("Second", issue.Location ?? string.Empty, StringComparison.Ordinal);
    }
}
