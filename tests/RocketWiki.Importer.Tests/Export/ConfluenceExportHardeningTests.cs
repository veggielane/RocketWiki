using System.IO.Compression;
using System.Text;
using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Tests.Export;

/// <summary>
/// The export archive is produced by <b>another organisation's</b> Confluence and handed
/// to an operator on this network. Two things follow that the reader did not used to
/// enforce:
///
/// <list type="number">
/// <item><b>It is a zip, and every structural check happens after decompression.</b> Is
/// there an entities.xml, is there exactly one Space object — all of it reads bytes first,
/// so none of it stops a few hundred kilobytes of compressed zeroes from expanding into
/// the importing host's memory. That is an availability hit on the regulated network,
/// available to whoever supplies the file.</item>
/// <item><b>It is XML.</b> External entities and DTD expansion were prohibited only by a
/// framework default — safe today, and unwritten, so a runtime change or an edit reaching
/// for <c>XDocument.Load</c>'s convenience overload would flip it silently.</item>
/// </list>
///
/// <para>The ceilings are injected small here so the refusal can be proven with kilobytes;
/// production uses <see cref="ConfluenceExportLimits.Default"/>, asserted at the bottom.</para>
/// </summary>
public class ConfluenceExportHardeningTests
{
    private const string MinimalEntitiesXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <hibernate-generic-datastore>
          <object class="Space">
            <id name="id">1</id>
            <property name="key"><![CDATA[ENG]]></property>
            <property name="name"><![CDATA[Engineering]]></property>
          </object>
        </hibernate-generic-datastore>
        """;

    // --- Decompression bounds -----------------------------------------------------------

    /// <summary>
    /// The declared size of entities.xml, refused before it is read. Free to check, and it
    /// stops the honest-but-enormous case without touching a byte.
    ///
    /// <para>Mutation-tested: remove the <c>entitiesEntry.Length</c> check and this still
    /// fails — via the streaming bound below, which is the belt-and-braces working.
    /// Remove <b>both</b> and the reader parses whatever it is handed.</para>
    /// </summary>
    [Fact]
    public void EntitiesXml_OverItsCeiling_IsRefused()
    {
        using var zip = BuildZip(MinimalEntitiesXml);

        var thrown = Assert.Throws<ConfluenceExportFormatException>(
            () => new ConfluenceXmlExportReader(new ConfluenceExportLimits { MaxEntitiesXmlBytes = 32 }).Read(zip));

        Assert.Contains("entities.xml", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Entry count, from the central directory alone.</summary>
    [Fact]
    public void TooManyEntries_IsRefused()
    {
        using var zip = BuildZip(MinimalEntitiesXml, ("attachments/300/1", new byte[16]));

        var thrown = Assert.Throws<ConfluenceExportFormatException>(
            () => new ConfluenceXmlExportReader(new ConfluenceExportLimits { MaxEntryCount = 1 }).Read(zip));

        Assert.Contains("entries", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The sum across entries — the per-entry ceilings alone would still admit
    /// MaxEntryCount × MaxAttachmentBytes.</summary>
    [Fact]
    public void TotalDeclaredExpansion_OverCeiling_IsRefused()
    {
        using var zip = BuildZip(MinimalEntitiesXml, ("attachments/300/1", new byte[8192]));

        var thrown = Assert.Throws<ConfluenceExportFormatException>(
            () => new ConfluenceXmlExportReader(new ConfluenceExportLimits { MaxTotalUncompressedBytes = 1024 }).Read(zip));

        Assert.Contains("uncompressed", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An attachment's bytes are bounded at the point of USE, not at read time: the
    /// closure the reader hands out is what pass 2 gives to AttachmentService, which
    /// buffers the whole attachment to hash it. Unbounded, that is a memory exhaustion
    /// wearing an upload's clothes.
    ///
    /// <para>The refusal surfaces from the stream itself, so it reaches the importer, which
    /// isolates it to the one attachment (ConfluenceSpaceImporter) rather than abandoning
    /// a part-committed run.</para>
    /// </summary>
    [Fact]
    public void AttachmentStream_OverItsCeiling_RefusesWhileBeingRead()
    {
        using var zip = BuildZip(EntitiesXmlWithAttachment, ("attachments/300/1", new byte[4096]));
        using var export = new ConfluenceXmlExportReader(
            new ConfluenceExportLimits { MaxAttachmentBytes = 64 }).Read(zip);

        var attachment = Assert.Single(Assert.Single(export.Space.Pages).Attachments);

        using var content = attachment.OpenContent();
        using var sink = new MemoryStream();
        Assert.Throws<RocketWiki.Core.Content.DecompressionLimitExceededException>(() => content.CopyTo(sink));
    }

    /// <summary>The control: an ordinary export, within the ceilings, still reads and its
    /// attachment bytes still come out whole.</summary>
    [Fact]
    public void OrdinaryExport_ReadsAndItsAttachmentBytesSurvive()
    {
        var bytes = "attachment payload"u8.ToArray();
        using var zip = BuildZip(EntitiesXmlWithAttachment, ("attachments/300/1", bytes));
        using var export = new ConfluenceXmlExportReader().Read(zip);

        var attachment = Assert.Single(Assert.Single(export.Space.Pages).Attachments);

        using var content = attachment.OpenContent();
        using var sink = new MemoryStream();
        content.CopyTo(sink);
        Assert.Equal(bytes, sink.ToArray());
    }

    [Fact]
    public void DefaultLimits_AreTheDocumentedCeilings()
    {
        var limits = ConfluenceExportLimits.Default;

        Assert.Equal(200_000, limits.MaxEntryCount);
        Assert.Equal(256L * 1024 * 1024, limits.MaxEntitiesXmlBytes);
        Assert.Equal(256L * 1024 * 1024, limits.MaxAttachmentBytes);
        Assert.Equal(32L * 1024 * 1024 * 1024, limits.MaxTotalUncompressedBytes);
    }

    // --- XXE ----------------------------------------------------------------------------

    /// <summary>
    /// An external entity referencing a local file. With <c>DtdProcessing.Prohibit</c> the
    /// DOCTYPE itself is the error, so the file is never opened and never substituted — the
    /// refusal arrives as the reader's own format exception rather than as a page whose
    /// body is somebody's /etc/passwd.
    ///
    /// <para>Mutation-tested: set <c>DtdProcessing = DtdProcessing.Parse</c> with a
    /// resolver and this stops throwing.</para>
    /// </summary>
    [Fact]
    public void EntitiesXml_DeclaringAnExternalEntity_IsRefused()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), $"rw-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secretPath, "TOP-SECRET-MARKER");
        try
        {
            var hostile = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE hibernate-generic-datastore [
                  <!ENTITY xxe SYSTEM "file:///{secretPath.Replace('\\', '/')}">
                ]>
                <hibernate-generic-datastore>
                  <object class="Space">
                    <id name="id">1</id>
                    <property name="key">&xxe;</property>
                  </object>
                </hibernate-generic-datastore>
                """;

            using var zip = BuildZip(hostile);

            var thrown = Assert.Throws<ConfluenceExportFormatException>(() => new ConfluenceXmlExportReader().Read(zip));
            Assert.DoesNotContain("TOP-SECRET-MARKER", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secretPath);
        }
    }

    /// <summary>The billion-laughs shape. Prohibiting the DTD removes the declaration
    /// site, so there is nothing to expand — a bound that holds without needing to count
    /// anything.</summary>
    [Fact]
    public void EntitiesXml_WithARecursiveEntityBomb_IsRefused()
    {
        var bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol1 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol2 "&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
            ]>
            <hibernate-generic-datastore><object class="Space"><id name="id">1</id>
            <property name="key">&lol3;</property></object></hibernate-generic-datastore>
            """;

        using var zip = BuildZip(bomb);

        Assert.Throws<ConfluenceExportFormatException>(() => new ConfluenceXmlExportReader().Read(zip));
    }

    // --- Fixtures ------------------------------------------------------------------------

    private const string EntitiesXmlWithAttachment = """
        <?xml version="1.0" encoding="UTF-8"?>
        <hibernate-generic-datastore>
          <object class="Space">
            <id name="id">1</id>
            <property name="key"><![CDATA[ENG]]></property>
            <property name="name"><![CDATA[Engineering]]></property>
          </object>
          <object class="Page">
            <id name="id">100</id>
            <property name="title"><![CDATA[Home]]></property>
            <property name="space"><id name="id">1</id></property>
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
          <object class="BodyContent">
            <id name="id">200</id>
            <property name="body"><![CDATA[<p>Welcome home.</p>]]></property>
            <property name="bodyType"><![CDATA[2]]></property>
          </object>
          <object class="Attachment">
            <id name="id">300</id>
            <property name="fileName"><![CDATA[diagram.bin]]></property>
            <property name="contentType"><![CDATA[application/octet-stream]]></property>
          </object>
        </hibernate-generic-datastore>
        """;

    private static Stream BuildZip(string entitiesXml, (string Path, byte[] Bytes)? attachmentEntry = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entitiesEntry = archive.CreateEntry("entities.xml");
            // UTF8Encoding(false): a BOM here is a parse error, not the case under test.
            using (var writer = new StreamWriter(entitiesEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(entitiesXml);
            }

            if (attachmentEntry is { } attachment)
            {
                using var entryStream = archive.CreateEntry(attachment.Path).Open();
                entryStream.Write(attachment.Bytes);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
