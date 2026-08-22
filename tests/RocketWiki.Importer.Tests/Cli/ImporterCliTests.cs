using System.IO.Compression;
using System.Text;
using RocketWiki.Importer.Cli;

namespace RocketWiki.Importer.Tests.Cli;

/// <summary>
/// Exercises the CLI end-to-end for --dry-run (which has no database dependency — see
/// ImporterCli's remarks) and argument validation for the real-run path, which must fail
/// before ever touching a database when required arguments are missing.
/// </summary>
public class ImporterCliTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("rocketwiki-importer-cli-tests").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteExportZip(string entitiesXml)
    {
        var path = Path.Combine(_tempDir, "export.zip");
        using var fileStream = File.Create(path);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("entities.xml");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(entitiesXml);
        return path;
    }

    private const string CleanEntitiesXml = """
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
            <property name="contentStatus"><![CDATA[current]]></property>
            <property name="bodyContents">
              <collection class="list">
                <element class="BodyContent"><id name="id">200</id></element>
              </collection>
            </property>
          </object>
          <object class="BodyContent">
            <id name="id">200</id>
            <property name="body"><![CDATA[<p>Welcome home.</p>]]></property>
            <property name="bodyType"><![CDATA[2]]></property>
          </object>
        </hibernate-generic-datastore>
        """;

    private static string LossyEntitiesXml(string title) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <hibernate-generic-datastore>
          <object class="Space">
            <id name="id">1</id>
            <property name="key"><![CDATA[ENG]]></property>
            <property name="name"><![CDATA[Engineering]]></property>
          </object>
          <object class="Page">
            <id name="id">100</id>
            <property name="title"><![CDATA[{{title}}]]></property>
            <property name="contentStatus"><![CDATA[current]]></property>
            <property name="bodyContents">
              <collection class="list">
                <element class="BodyContent"><id name="id">200</id></element>
              </collection>
            </property>
          </object>
          <object class="BodyContent">
            <id name="id">200</id>
            <property name="body"><![CDATA[<p><u>underlined</u></p>]]></property>
            <property name="bodyType"><![CDATA[2]]></property>
          </object>
        </hibernate-generic-datastore>
        """;

    [Fact]
    public async Task Dry_run_on_a_clean_export_writes_a_report_and_returns_zero()
    {
        var exportPath = WriteExportZip(CleanEntitiesXml);
        var reportPath = Path.Combine(_tempDir, "report.txt");
        var output = new StringWriter();

        var exitCode = await ImporterCli.RunAsync(
            ["--export", exportPath, "--space-key", "ENG", "--dry-run", "--report", reportPath], output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(reportPath));
        var reportText = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("DRY RUN", reportText);
        Assert.Contains("Home", reportText);
        Assert.Contains("Pages needing review (0)", reportText);
    }

    [Fact]
    public async Task Dry_run_on_lossy_content_returns_the_needs_review_exit_code()
    {
        var exportPath = WriteExportZip(LossyEntitiesXml("Lossy Page"));
        var reportPath = Path.Combine(_tempDir, "report.txt");

        var exitCode = await ImporterCli.RunAsync(
            ["--export", exportPath, "--space-key", "ENG", "--dry-run", "--report", reportPath], new StringWriter());

        Assert.Equal(3, exitCode);
        var reportText = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("Underline", reportText);
        Assert.Contains("Pages needing review (1)", reportText);
    }

    [Fact]
    public async Task Report_path_defaults_next_to_the_export_file_when_not_given()
    {
        var exportPath = WriteExportZip(CleanEntitiesXml);

        var exitCode = await ImporterCli.RunAsync(["--export", exportPath, "--space-key", "ENG", "--dry-run"], new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(exportPath + "-import-report.txt"));
    }

    [Fact]
    public async Task Mismatched_space_key_refuses_to_proceed()
    {
        var exportPath = WriteExportZip(CleanEntitiesXml);
        var output = new StringWriter();

        var exitCode = await ImporterCli.RunAsync(["--export", exportPath, "--space-key", "WRONG", "--dry-run"], output);

        Assert.Equal(1, exitCode);
        Assert.Contains("does not match", output.ToString());
    }

    [Fact]
    public async Task Nonexistent_export_file_fails_clearly_rather_than_throwing()
    {
        var output = new StringWriter();

        var exitCode = await ImporterCli.RunAsync(["--export", Path.Combine(_tempDir, "missing.zip"), "--space-key", "ENG", "--dry-run"], output);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", output.ToString());
    }

    [Fact]
    public async Task Missing_required_arguments_prints_usage_and_returns_one()
    {
        var output = new StringWriter();

        var exitCode = await ImporterCli.RunAsync([], output);

        Assert.Equal(1, exitCode);
        Assert.Contains("RocketWiki.Importer", output.ToString());
    }

    [Fact]
    public async Task Real_run_missing_required_grant_arguments_fails_before_touching_any_database()
    {
        var exportPath = WriteExportZip(CleanEntitiesXml);
        var output = new StringWriter();

        // No --dry-run and none of the real-run-only arguments supplied - must fail
        // argument validation, never reach the point of constructing a DbContext.
        var exitCode = await ImporterCli.RunAsync(["--export", exportPath, "--space-key", "ENG"], output);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Malformed_export_produces_a_clear_error_not_an_unhandled_exception()
    {
        var path = Path.Combine(_tempDir, "not-an-export.zip");
        using (var fileStream = File.Create(path))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        var output = new StringWriter();
        var exitCode = await ImporterCli.RunAsync(["--export", path, "--space-key", "ENG", "--dry-run"], output);

        Assert.Equal(1, exitCode);
        Assert.Contains("entities.xml", output.ToString());
    }
}
