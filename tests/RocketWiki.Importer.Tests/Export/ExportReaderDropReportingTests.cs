using System.IO.Compression;
using System.Text;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;

namespace RocketWiki.Importer.Tests.Export;

/// <summary>
/// Everything the reader excludes has to be visible somewhere, because the import
/// report's "N of M pages" cannot show it: M is <c>Pages.Count</c>, which is already
/// post-filter, so a dropped blog post or a comment that could not be placed changed
/// neither number. A migration is judged on what did NOT arrive.
///
/// <para>The comment case is the sharpest: RUNBOOK §5 already warns that a
/// property-name mismatch in the export makes replies "silently vanish", and there was
/// no counter that would have shown it — the warning and the silence shipped
/// together.</para>
/// </summary>
public class ExportReaderDropReportingTests
{
    private static Stream BuildExportZip(string entitiesXml)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("entities.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(entitiesXml);
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Blog_posts_the_reader_cannot_import_are_reported()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="Page">
                <id name="id">100</id>
                <property name="title"><![CDATA[Home]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
              <object class="BlogPost">
                <id name="id">200</id>
                <property name="title"><![CDATA[Launch retrospective]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
              <object class="BlogPost">
                <id name="id">201</id>
                <property name="title"><![CDATA[Q3 update]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Contains(export.Space.ReaderNotes, n => n.Contains("2 blog post", StringComparison.Ordinal));
    }

    [Fact]
    public void An_attachment_whose_binary_is_missing_from_the_zip_is_reported()
    {
        // An incomplete export. Before this it was invisible unless a page happened to
        // reference the attachment and a reviewer noticed the broken link.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="Page">
                <id name="id">100</id>
                <property name="title"><![CDATA[Home]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
                <property name="attachments">
                  <collection class="list">
                    <element class="Attachment"><id name="id">300</id></element>
                  </collection>
                </property>
              </object>
              <object class="Attachment">
                <id name="id">300</id>
                <property name="fileName"><![CDATA[turbopump-spec.pdf]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Empty(Assert.Single(export.Space.Pages).Attachments);
        Assert.Contains(export.Space.ReaderNotes, n => n.Contains("turbopump-spec.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void Comments_whose_owning_page_cannot_be_resolved_are_counted()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="Page">
                <id name="id">100</id>
                <property name="title"><![CDATA[Home]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
              <object class="Comment">
                <id name="id">400</id>
                <property name="bodyContents">
                  <collection class="list">
                    <element class="BodyContent"><id name="id">500</id></element>
                  </collection>
                </property>
              </object>
              <object class="BodyContent">
                <id name="id">500</id>
                <property name="body"><![CDATA[<p>Orphaned.</p>]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Empty(Assert.Single(export.Space.Pages).Comments);
        Assert.Contains(export.Space.ReaderNotes, n => n.Contains("1 comment(s) were dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void The_confluence_home_page_is_reported_since_the_import_sets_none()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
                <property name="homePage"><id name="id">100</id></property>
              </object>
              <object class="Page">
                <id name="id">100</id>
                <property name="title"><![CDATA[Engineering Home]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Contains(export.Space.ReaderNotes, n => n.Contains("Engineering Home", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_export_produces_no_reader_notes()
    {
        // The other half: these notes must mean something when they appear, so an export
        // with nothing dropped must produce none of them.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="Page">
                <id name="id">100</id>
                <property name="title"><![CDATA[Home]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Empty(export.Space.ReaderNotes);
    }

    [Fact]
    public void Reader_notes_reach_the_import_report()
    {
        // A field nobody prints is the same silence with more code: the notes have to
        // land in the report a content owner actually reads.
        var export = new ConfluenceExportSpace("ENG", "Engineering", null,
            [new ConfluenceExportPage("1", null, "Home", "<p>Hi</p>", null, null, [])],
            Permissions: null,
            ReaderNotes: ["3 blog post(s) in the export were not imported."]);

        var result = new ConfluenceImportValidator().Validate(export);

        Assert.Contains(result.Report.PipelineNotes, n => n.Contains("3 blog post(s)", StringComparison.Ordinal));
        Assert.True(result.Report.NeedsReview);
    }
}
