namespace RocketWiki.Importer.Tests.Corpus;

/// <summary>
/// One named, checked-in-corpus-worthy fixture: Confluence storage XHTML, the resolver
/// setup it needs (if any), and the page context to convert it under.
/// </summary>
public sealed record CorpusFixture(
    string Name,
    string Xhtml,
    Action<FakePageIdResolver>? ConfigureResolver = null,
    ConfluencePageContext? Context = null);

/// <summary>
/// design.md §13: "the converter's outputs are checked in as a shared corpus, and the
/// editor's round-trip suite runs against that corpus as well as its own fixtures." This
/// is that corpus's source of truth — every fixture here is regenerated into
/// tests/fixtures/converted-markdown/{Name}.md on every test run (see
/// CorpusGenerationTests), so the checked-in .md files can never hand-drift from what the
/// converter actually produces.
///
/// Scope deliberately excludes fixtures whose Markdown output is empty (an unresolvable
/// image, a content-less unknown macro) or that don't produce Markdown at all (malformed
/// input) — there is nothing for a round-trip suite to round-trip in either case.
/// </summary>
public static class ConversionCorpus
{
    public static IReadOnlyList<CorpusFixture> Fixtures { get; } =
    [
        new CorpusFixture(
            "heading-and-paragraph",
            "<h1>Title</h1><p>Hello world.</p>"),

        new CorpusFixture(
            "formatting-bold-italic-strike-code",
            "<p><strong>bold</strong> <em>italic</em> <s>struck</s> and <code>inline code</code>.</p>"),

        new CorpusFixture(
            "external-link",
            "<p>See <a href=\"https://example.com/docs\">the docs</a> for more.</p>"),

        new CorpusFixture(
            "blockquote-multi-paragraph",
            "<blockquote><p>First para.</p><p>Second para.</p></blockquote>"),

        new CorpusFixture(
            "unordered-list",
            "<ul><li><p>First</p></li><li><p>Second</p></li><li><p>Third</p></li></ul>"),

        new CorpusFixture(
            "ordered-list",
            "<ol><li><p>First</p></li><li><p>Second</p></li><li><p>Third</p></li></ol>"),

        new CorpusFixture(
            "nested-unordered-list",
            """
            <ul>
              <li><p>Item 1</p></li>
              <li><p>Item 2</p>
                <ul>
                  <li><p>Nested A</p></li>
                  <li><p>Nested B</p></li>
                </ul>
              </li>
            </ul>
            """),

        new CorpusFixture(
            "task-list",
            """
            <ac:task-list>
              <ac:task>
                <ac:task-status>complete</ac:task-status>
                <ac:task-body>Write the converter</ac:task-body>
              </ac:task>
              <ac:task>
                <ac:task-status>incomplete</ac:task-status>
                <ac:task-body>Write the tests</ac:task-body>
              </ac:task>
            </ac:task-list>
            """),

        new CorpusFixture(
            "table-basic",
            """
            <table>
              <tbody>
                <tr><th>Name</th><th>Role</th></tr>
                <tr><td>Ada</td><td>Engineer</td></tr>
                <tr><td>Grace</td><td>Admiral</td></tr>
              </tbody>
            </table>
            """),

        new CorpusFixture(
            "table-merged-cells-degraded",
            """
            <table>
              <tbody>
                <tr><th>A</th><th>B</th><th>C</th></tr>
                <tr><td colspan="2">Merged AB</td><td>c1</td></tr>
              </tbody>
            </table>
            """),

        new CorpusFixture(
            "code-macro-with-language",
            """
            <ac:structured-macro ac:name="code" ac:schema-version="1">
              <ac:parameter ac:name="language">csharp</ac:parameter>
              <ac:plain-text-body><![CDATA[public class Foo {}]]></ac:plain-text-body>
            </ac:structured-macro>
            """),

        new CorpusFixture(
            "code-macro-without-language",
            """
            <ac:structured-macro ac:name="code" ac:schema-version="1">
              <ac:plain-text-body><![CDATA[echo hello]]></ac:plain-text-body>
            </ac:structured-macro>
            """),

        new CorpusFixture(
            "callout-info",
            """
            <ac:structured-macro ac:name="info" ac:schema-version="1">
              <ac:rich-text-body><p>Heads up.</p></ac:rich-text-body>
            </ac:structured-macro>
            """),

        new CorpusFixture(
            "callout-warning-with-title",
            """
            <ac:structured-macro ac:name="warning" ac:schema-version="1">
              <ac:parameter ac:name="title">Danger zone</ac:parameter>
              <ac:rich-text-body><p>Proceed carefully.</p></ac:rich-text-body>
            </ac:structured-macro>
            """),

        new CorpusFixture(
            "callout-tip-remapped-to-info",
            """
            <ac:structured-macro ac:name="tip" ac:schema-version="1">
              <ac:rich-text-body><p>Handy trick.</p></ac:rich-text-body>
            </ac:structured-macro>
            """),

        new CorpusFixture(
            "expand-macro-flattened",
            """
            <ac:structured-macro ac:name="expand" ac:schema-version="1">
              <ac:parameter ac:name="title">Click to expand</ac:parameter>
              <ac:rich-text-body><p>Hidden details.</p></ac:rich-text-body>
            </ac:structured-macro>
            """),

        new CorpusFixture(
            "status-macro-inline-code",
            """
            <p>Ticket state:
            <ac:structured-macro ac:name="status" ac:schema-version="1">
              <ac:parameter ac:name="colour">Green</ac:parameter>
              <ac:parameter ac:name="title">DONE</ac:parameter>
            </ac:structured-macro>
            </p>
            """),

        new CorpusFixture(
            "page-link-resolved",
            """<p>See <ac:link><ri:page ri:content-title="Other Page" /><ac:plain-text-link-body><![CDATA[the runbook]]></ac:plain-text-link-body></ac:link>.</p>""",
            resolver => resolver.WithPage("Other Page", "22222222-2222-2222-2222-222222222222")),

        new CorpusFixture(
            "attachment-image-resolved",
            """<ac:image ac:alt="Architecture diagram"><ri:attachment ri:filename="diagram.png" /></ac:image>""",
            resolver => resolver.WithAttachment("diagram.png", "33333333-3333-3333-3333-333333333333")),

        new CorpusFixture(
            "attachment-link-resolved",
            """<p>See the <ac:link><ri:attachment ri:filename="spec.pdf" /><ac:plain-text-link-body><![CDATA[spec PDF]]></ac:plain-text-link-body></ac:link>.</p>""",
            resolver => resolver.WithAttachment("spec.pdf", "44444444-4444-4444-4444-444444444444")),

        new CorpusFixture(
            "composite-deployment-guide",
            """
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
            """,
            resolver => resolver.WithPage("Runbook", "11111111-1111-1111-1111-111111111111")),
    ];

    public static IEnumerable<object[]> AsTheoryData() => Fixtures.Select(f => new object[] { f });
}
