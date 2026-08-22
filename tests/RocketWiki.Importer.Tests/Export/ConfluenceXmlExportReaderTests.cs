using System.IO.Compression;
using System.Text;
using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Tests.Export;

/// <summary>
/// Exercises ConfluenceXmlExportReader against a hand-built in-memory zip rather than a
/// real Confluence export file (none was available — see the reader's own remarks on
/// that gap). These tests prove the reader correctly walks the generic
/// object/id/property/collection shape and wires pages, bodies, attachments, and authors
/// together the way this class assumes a real export does; they cannot prove that
/// assumption matches Confluence's actual output.
/// </summary>
public class ConfluenceXmlExportReaderTests
{
    private const string EntitiesXmlTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <hibernate-generic-datastore>
          <object class="Space">
            <id name="id">1</id>
            <property name="key"><![CDATA[ENG]]></property>
            <property name="name"><![CDATA[Engineering]]></property>
            <property name="description"><![CDATA[Engineering docs]]></property>
          </object>
          <object class="ConfluenceUserImpl">
            <id name="id">50</id>
            <property name="email"><![CDATA[alice@example.com]]></property>
            <property name="fullName"><![CDATA[Alice Example]]></property>
          </object>
          <object class="Page">
            <id name="id">100</id>
            <property name="title"><![CDATA[Home]]></property>
            <property name="space"><id name="id">1</id></property>
            <property name="creator"><id name="id">50</id></property>
            <property name="creationDate"><![CDATA[2020-01-01 00:00:00.0]]></property>
            <property name="contentStatus"><![CDATA[current]]></property>
            <property name="bodyContents">
              <collection class="list">
                <element class="BodyContent"><id name="id">200</id></element>
              </collection>
            </property>
            <property name="attachments">
              <collection class="list">
                <element class="Attachment"><id name="id">300</id></element>
              </collection>
            </property>
          </object>
          <object class="Page">
            <id name="id">101</id>
            <property name="title"><![CDATA[Child Page]]></property>
            <property name="space"><id name="id">1</id></property>
            <property name="parent"><id name="id">100</id></property>
            <property name="contentStatus"><![CDATA[current]]></property>
            <property name="bodyContents">
              <collection class="list">
                <element class="BodyContent"><id name="id">201</id></element>
              </collection>
            </property>
          </object>
          <object class="Page">
            <id name="id">102</id>
            <property name="title"><![CDATA[Old Draft]]></property>
            <property name="space"><id name="id">1</id></property>
            <property name="contentStatus"><![CDATA[draft]]></property>
            <property name="bodyContents">
              <collection class="list">
                <element class="BodyContent"><id name="id">202</id></element>
              </collection>
            </property>
          </object>
          <object class="BodyContent">
            <id name="id">200</id>
            <property name="body"><![CDATA[<p>Welcome home.</p>]]></property>
            <property name="bodyType"><![CDATA[2]]></property>
          </object>
          <object class="BodyContent">
            <id name="id">201</id>
            <property name="body"><![CDATA[<p>Child content.</p>]]></property>
            <property name="bodyType"><![CDATA[2]]></property>
          </object>
          <object class="BodyContent">
            <id name="id">202</id>
            <property name="body"><![CDATA[<p>Draft content.</p>]]></property>
            <property name="bodyType"><![CDATA[2]]></property>
          </object>
          <object class="Attachment">
            <id name="id">300</id>
            <property name="fileName"><![CDATA[diagram.png]]></property>
            <property name="contentType"><![CDATA[image/png]]></property>
            <property name="content"><id name="id">100</id></property>
          </object>
        </hibernate-generic-datastore>
        """;

    private static readonly byte[] AttachmentBytes = [1, 2, 3, 4, 5, 250, 251, 252];

    private static Stream BuildExportZip(string entitiesXml, (string Path, byte[] Bytes)? attachmentEntry = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entitiesEntry = archive.CreateEntry("entities.xml");
            using (var writer = new StreamWriter(entitiesEntry.Open(), Encoding.UTF8))
            {
                writer.Write(entitiesXml);
            }

            if (attachmentEntry is { } attachment)
            {
                var entry = archive.CreateEntry(attachment.Path);
                using var entryStream = entry.Open();
                entryStream.Write(attachment.Bytes);
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Reads_space_key_name_and_description()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        Assert.Equal("ENG", export.Space.Key);
        Assert.Equal("Engineering", export.Space.Name);
        Assert.Equal("Engineering docs", export.Space.Description);
    }

    [Fact]
    public void Reads_pages_with_parent_relationship_and_storage_body()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        var home = Assert.Single(export.Space.Pages, p => p.ConfluencePageId == "100");
        Assert.Null(home.ParentConfluencePageId);
        Assert.Equal("Home", home.Title);
        Assert.Equal("<p>Welcome home.</p>", home.StorageBodyXhtml);

        var child = Assert.Single(export.Space.Pages, p => p.ConfluencePageId == "101");
        Assert.Equal("100", child.ParentConfluencePageId);
        Assert.Equal("Child Page", child.Title);
    }

    [Fact]
    public void Non_current_content_status_pages_are_excluded()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        Assert.DoesNotContain(export.Space.Pages, p => p.ConfluencePageId == "102");
    }

    [Fact]
    public void Reads_the_creator_as_an_author_with_email_and_display_name()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        var home = Assert.Single(export.Space.Pages, p => p.ConfluencePageId == "100");
        Assert.NotNull(home.Author);
        Assert.Equal("alice@example.com", home.Author!.Email);
        Assert.Equal("Alice Example", home.Author.DisplayName);
    }

    [Fact]
    public void Page_with_no_creator_property_has_a_null_author()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        var child = Assert.Single(export.Space.Pages, p => p.ConfluencePageId == "101");
        Assert.Null(child.Author);
    }

    [Fact]
    public void Reads_attachment_metadata_and_binary_content_matching_the_zip_entry()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        var home = Assert.Single(export.Space.Pages, p => p.ConfluencePageId == "100");
        var attachment = Assert.Single(home.Attachments);
        Assert.Equal("diagram.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);

        using var content = attachment.OpenContent();
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        Assert.Equal(AttachmentBytes, buffer.ToArray());
    }

    [Fact]
    public void Page_with_no_attachments_property_has_an_empty_attachment_list()
    {
        using var zip = BuildExportZip(EntitiesXmlTemplate, ("attachments/300/1", AttachmentBytes));
        var reader = new ConfluenceXmlExportReader();

        using var export = reader.Read(zip);

        var child = Assert.Single(export.Space.Pages, p => p.ConfluencePageId == "101");
        Assert.Empty(child.Attachments);
    }

    [Fact]
    public void Missing_entities_xml_throws_a_named_format_exception()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("some-other-file.txt");
        }

        stream.Position = 0;
        var reader = new ConfluenceXmlExportReader();

        Assert.Throws<ConfluenceExportFormatException>(() => reader.Read(stream));
    }

    [Fact]
    public void Entities_xml_with_no_space_object_throws_a_named_format_exception()
    {
        const string xmlWithNoSpace = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Page">
                <id name="id">100</id>
                <property name="title"><![CDATA[Home]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xmlWithNoSpace);
        var reader = new ConfluenceXmlExportReader();

        Assert.Throws<ConfluenceExportFormatException>(() => reader.Read(zip));
    }

    [Fact]
    public void Entities_xml_with_more_than_one_space_object_throws_a_clear_diagnostic_rather_than_picking_one_silently()
    {
        const string xmlWithTwoSpaces = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="Space">
                <id name="id">2</id>
                <property name="key"><![CDATA[MKT]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xmlWithTwoSpaces);
        var reader = new ConfluenceXmlExportReader();

        var ex = Assert.Throws<ConfluenceExportFormatException>(() => reader.Read(zip));
        Assert.Contains("2 Space objects", ex.Message);
    }
}
