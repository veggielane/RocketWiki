using System.IO.Compression;
using System.Text;
using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Tests.Export;

public class ConfluenceXmlExportReaderCommentsAndLabelsTests
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
    public void Top_level_comment_resolves_to_its_owning_page_with_no_parent()
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
                <property name="owner"><id name="id">100</id></property>
                <property name="bodyContents">
                  <collection class="list">
                    <element class="BodyContent"><id name="id">500</id></element>
                  </collection>
                </property>
              </object>
              <object class="BodyContent">
                <id name="id">500</id>
                <property name="body"><![CDATA[<p>Nice page!</p>]]></property>
                <property name="bodyType"><![CDATA[2]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var home = Assert.Single(export.Space.Pages);
        var comment = Assert.Single(home.Comments);
        Assert.Equal("400", comment.ConfluenceCommentId);
        Assert.Null(comment.ParentConfluenceCommentId);
        Assert.Equal("<p>Nice page!</p>", comment.BodyXhtml);
    }

    [Fact]
    public void Threaded_reply_resolves_to_the_same_page_with_its_parent_comment_set()
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
                <property name="owner"><id name="id">100</id></property>
                <property name="bodyContents">
                  <collection class="list"><element class="BodyContent"><id name="id">500</id></element></collection>
                </property>
              </object>
              <object class="Comment">
                <id name="id">401</id>
                <property name="owner"><id name="id">100</id></property>
                <property name="parent"><id name="id">400</id></property>
                <property name="bodyContents">
                  <collection class="list"><element class="BodyContent"><id name="id">501</id></element></collection>
                </property>
              </object>
              <object class="BodyContent">
                <id name="id">500</id>
                <property name="body"><![CDATA[<p>Root comment.</p>]]></property>
                <property name="bodyType"><![CDATA[2]]></property>
              </object>
              <object class="BodyContent">
                <id name="id">501</id>
                <property name="body"><![CDATA[<p>A reply.</p>]]></property>
                <property name="bodyType"><![CDATA[2]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var home = Assert.Single(export.Space.Pages);
        Assert.Equal(2, home.Comments.Count);
        var reply = home.Comments.Single(c => c.ConfluenceCommentId == "401");
        Assert.Equal("400", reply.ParentConfluenceCommentId);
    }

    [Fact]
    public void Comment_with_no_resolvable_owning_page_is_dropped_rather_than_guessed_at()
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
                  <collection class="list"><element class="BodyContent"><id name="id">500</id></element></collection>
                </property>
              </object>
              <object class="BodyContent">
                <id name="id">500</id>
                <property name="body"><![CDATA[<p>Orphaned comment.</p>]]></property>
                <property name="bodyType"><![CDATA[2]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var home = Assert.Single(export.Space.Pages);
        Assert.Empty(home.Comments);
    }

    [Fact]
    public void Labels_via_direct_labels_collection_resolve_and_strip_namespace_prefixes()
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
                <property name="labels">
                  <collection class="list">
                    <element class="Label"><id name="id">600</id></element>
                    <element class="Label"><id name="id">601</id></element>
                  </collection>
                </property>
              </object>
              <object class="Label">
                <id name="id">600</id>
                <property name="name"><![CDATA[global:how-to]]></property>
              </object>
              <object class="Label">
                <id name="id">601</id>
                <property name="name"><![CDATA[important]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var home = Assert.Single(export.Space.Pages);
        Assert.Equal(["how-to", "important"], home.Labels.OrderBy(l => l, StringComparer.Ordinal));
    }

    [Fact]
    public void Labels_via_indirect_labellings_join_collection_also_resolve()
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
                <property name="labellings">
                  <collection class="list">
                    <element class="Labelling"><id name="id">700</id></element>
                  </collection>
                </property>
              </object>
              <object class="Labelling">
                <id name="id">700</id>
                <property name="label"><id name="id">600</id></property>
              </object>
              <object class="Label">
                <id name="id">600</id>
                <property name="name"><![CDATA[how-to]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var home = Assert.Single(export.Space.Pages);
        Assert.Equal(["how-to"], home.Labels);
    }

    [Fact]
    public void Page_with_no_labels_or_labellings_property_has_an_empty_label_list()
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
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Empty(export.Space.Pages.Single().Labels);
    }
}
