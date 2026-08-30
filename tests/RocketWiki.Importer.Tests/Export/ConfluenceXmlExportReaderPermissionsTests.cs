using System.IO.Compression;
using System.Text;
using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Tests.Export;

/// <summary>
/// design.md §13: "Confluence permissions are reported, never translated." The reporting
/// half starts here — a permission the reader never looks for cannot be reported, and the
/// resulting import silently lands restricted content in a space governed only by the
/// operator's grant expression. That is the over-open direction §13 names as the worse of
/// the two failures, so these tests are about what survives the read <b>verbatim</b>,
/// including shapes this reader has no opinion about.
/// </summary>
public class ConfluenceXmlExportReaderPermissionsTests
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
    public void Space_permissions_are_read_verbatim_for_groups_users_and_anonymous()
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
              <object class="ConfluenceUserImpl">
                <id name="id">900</id>
                <property name="email"><![CDATA[ada@example.test]]></property>
                <property name="fullName"><![CDATA[Ada Lovelace]]></property>
              </object>
              <object class="SpacePermission">
                <id name="id">700</id>
                <property name="type"><![CDATA[VIEWSPACE]]></property>
                <property name="group"><![CDATA[propulsion-engineers]]></property>
                <property name="space"><id name="id">1</id></property>
              </object>
              <object class="SpacePermission">
                <id name="id">701</id>
                <property name="type"><![CDATA[SETSPACEPERMISSIONS]]></property>
                <property name="userSubject"><id name="id">900</id></property>
                <property name="space"><id name="id">1</id></property>
              </object>
              <object class="SpacePermission">
                <id name="id">702</id>
                <property name="type"><![CDATA[VIEWSPACE]]></property>
                <property name="space"><id name="id">1</id></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        Assert.Equal(3, export.Space.Permissions.Count);

        var group = export.Space.Permissions[0];
        Assert.Equal("VIEWSPACE", group.Type);
        Assert.Equal("group", group.SubjectKind);
        Assert.Equal("propulsion-engineers", group.Subject);

        // Resolved to something an admin can recognise. A raw user key would be reported
        // rather than dropped, but it is not what a person re-applying these needs.
        var user = export.Space.Permissions[1];
        Assert.Equal("SETSPACEPERMISSIONS", user.Type);
        Assert.Equal("user", user.SubjectKind);
        Assert.Equal("ada@example.test", user.Subject);

        // No group, no user: Confluence's way of recording anonymous access, and the most
        // consequential line the report can carry.
        var anonymous = export.Space.Permissions[2];
        Assert.Equal("anonymous", anonymous.SubjectKind);
        Assert.Null(anonymous.Subject);
    }

    [Fact]
    public void A_permission_type_this_reader_has_never_heard_of_still_survives_the_read()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="SpacePermission">
                <id name="id">700</id>
                <property name="type"><![CDATA[SOMEFUTUREPERMISSION]]></property>
                <property name="group"><![CDATA[everyone]]></property>
                <property name="space"><id name="id">1</id></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        // Filtering to a known-types list would drop exactly the permission most worth a
        // human's attention, and would do it silently.
        var permission = Assert.Single(export.Space.Permissions);
        Assert.Equal("SOMEFUTUREPERMISSION", permission.Type);
        Assert.Equal("everyone", permission.Subject);
    }

    [Fact]
    public void Page_restrictions_are_read_and_attached_to_the_page_they_restrict()
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
                <property name="title"><![CDATA[Open Page]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
              <object class="Page">
                <id name="id">101</id>
                <property name="title"><![CDATA[Restricted Page]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
              <object class="ContentPermissionSet">
                <id name="id">800</id>
                <property name="type"><![CDATA[View]]></property>
                <property name="owningContent"><id name="id">101</id></property>
                <property name="contentPermissions">
                  <collection class="list">
                    <element class="ContentPermission"><id name="id">810</id></element>
                    <element class="ContentPermission"><id name="id">811</id></element>
                  </collection>
                </property>
              </object>
              <object class="ContentPermission">
                <id name="id">810</id>
                <property name="type"><![CDATA[View]]></property>
                <property name="groupName"><![CDATA[itar-cleared]]></property>
              </object>
              <object class="ContentPermission">
                <id name="id">811</id>
                <property name="userName"><![CDATA[grace.hopper]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var open = export.Space.Pages.Single(p => p.Title == "Open Page");
        Assert.Empty(open.Restrictions);

        var restricted = export.Space.Pages.Single(p => p.Title == "Restricted Page");
        Assert.Equal(2, restricted.Restrictions.Count);
        Assert.Equal("View", restricted.Restrictions[0].Type);
        Assert.Equal("group", restricted.Restrictions[0].SubjectKind);
        Assert.Equal("itar-cleared", restricted.Restrictions[0].Subject);

        // The entry omitted its own type; the set carries the action, so the restriction
        // is still reported as a View restriction rather than as an untyped one.
        Assert.Equal("View", restricted.Restrictions[1].Type);
        Assert.Equal("user", restricted.Restrictions[1].SubjectKind);
        Assert.Equal("grace.hopper", restricted.Restrictions[1].Subject);
    }

    [Fact]
    public void An_empty_restriction_set_still_marks_the_page_as_restricted()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="Page">
                <id name="id">101</id>
                <property name="title"><![CDATA[Restricted Page]]></property>
                <property name="contentStatus"><![CDATA[current]]></property>
              </object>
              <object class="ContentPermissionSet">
                <id name="id">800</id>
                <property name="type"><![CDATA[Edit]]></property>
                <property name="owningContent"><id name="id">101</id></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        // Confluence writes a set when a page is restricted. Reading the set but reporting
        // nothing because its entries were shaped unexpectedly would report the page as
        // unrestricted — the exact silent over-open outcome.
        var page = Assert.Single(export.Space.Pages);
        var restriction = Assert.Single(page.Restrictions);
        Assert.Equal("Edit", restriction.Type);
    }

    [Fact]
    public void Permissions_belonging_to_another_space_are_excluded()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hibernate-generic-datastore>
              <object class="Space">
                <id name="id">1</id>
                <property name="key"><![CDATA[ENG]]></property>
              </object>
              <object class="SpacePermission">
                <id name="id">700</id>
                <property name="type"><![CDATA[VIEWSPACE]]></property>
                <property name="group"><![CDATA[ours]]></property>
                <property name="space"><id name="id">1</id></property>
              </object>
              <object class="SpacePermission">
                <id name="id">701</id>
                <property name="type"><![CDATA[VIEWSPACE]]></property>
                <property name="group"><![CDATA[theirs]]></property>
                <property name="space"><id name="id">2</id></property>
              </object>
              <object class="SpacePermission">
                <id name="id">702</id>
                <property name="type"><![CDATA[VIEWSPACE]]></property>
                <property name="group"><![CDATA[unattributed]]></property>
              </object>
            </hibernate-generic-datastore>
            """;

        using var zip = BuildExportZip(xml);
        using var export = new ConfluenceXmlExportReader().Read(zip);

        // A permission naming a different space is genuinely not this space's business.
        // One naming NO space is kept: a single-space export has only one, and dropping
        // it because a property name drifted is the failure this whole finding is about.
        Assert.Equal(["ours", "unattributed"], export.Space.Permissions.Select(p => p.Subject));
    }
}
