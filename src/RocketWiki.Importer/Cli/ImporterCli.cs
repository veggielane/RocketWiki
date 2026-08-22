using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Reporting;
using RocketWiki.Storage;

namespace RocketWiki.Importer.Cli;

/// <summary>
/// The console tool's actual logic, separated from <c>Program.cs</c> so it can be driven
/// by tests with an injectable <see cref="TextWriter"/> instead of real stdout.
/// </summary>
/// <remarks>
/// The real-run path (everything past argument validation, once <c>--dry-run</c> is not
/// set) constructs <c>RocketWikiDbContext</c> and the four concrete
/// <c>RocketWiki.Data.Services</c> classes exactly as <c>RocketWiki.Api/Program.cs</c>
/// does — same constructors, same <c>localInstanceId</c> convention — but has not been
/// run end-to-end against a real SQL Server (none was available in this environment; see
/// design.md §16's standing caveat). The dry-run path has no such gap: it depends on
/// nothing but the export file.
/// </remarks>
public static class ImporterCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;

        var options = CliArgumentParser.Parse(args);
        if (options is null)
        {
            output.WriteLine(CliArgumentParser.Usage);
            return 1;
        }

        if (!File.Exists(options.ExportPath))
        {
            output.WriteLine($"Export file not found: {options.ExportPath}");
            return 1;
        }

        ConfluenceSpaceExport export;
        try
        {
            using var exportStream = File.OpenRead(options.ExportPath);
            export = new ConfluenceXmlExportReader().Read(exportStream);
        }
        catch (ConfluenceExportFormatException ex)
        {
            output.WriteLine($"Could not read the export: {ex.Message}");
            return 1;
        }

        using (export)
        {
            if (!string.Equals(export.Space.Key, options.ExpectedSpaceKey, StringComparison.Ordinal))
            {
                output.WriteLine(
                    $"--space-key '{options.ExpectedSpaceKey}' does not match the export's actual space key " +
                    $"'{export.Space.Key}'. Refusing to proceed - this usually means the wrong export file was given.");
                return 1;
            }

            ImportReport report;
            ImportValidationSummary summary;
            Guid? spaceId = null;
            string? blockedReason = null;

            if (options.DryRun)
            {
                var validation = new ConfluenceImportValidator().Validate(export.Space);
                report = validation.Report;
                summary = validation.Summary;
            }
            else
            {
                var optionsBuilder = new DbContextOptionsBuilder<RocketWikiDbContext>().UseSqlServer(options.ConnectionString);
                await using var dbContext = new RocketWikiDbContext(optionsBuilder.Options);

                var fileStorage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
                {
                    FileSystem = new FileSystemFileStorageOptions { Root = options.AttachmentsRoot },
                }));

                var spaceService = new SpaceService(dbContext, options.LocalInstanceId);
                var pageService = new PageService(dbContext, options.LocalInstanceId);
                var attachmentService = new AttachmentService(dbContext, fileStorage, options.LocalInstanceId);
                var commentService = new CommentService(dbContext, options.LocalInstanceId);
                var labelService = new LabelService(dbContext, options.LocalInstanceId);

                var importer = new ConfluenceSpaceImporter(spaceService, pageService, attachmentService, commentService, labelService);
                var importOptions = new ImportOptions(
                    Principal.Create(options.ImporterPrincipalUserId!, ["confluence-importer"]),
                    options.ActingUserId!.Value,
                    new AuditContext(AuditChannel.System, $"confluence-import:{export.Space.Key}:{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}", "127.0.0.1"),
                    new InitialSpaceGrant(options.InitialGrantRole!.Value, options.InitialGrantExpressionJson!));

                var result = await importer.ImportAsync(export.Space, importOptions, cancellationToken);
                report = result.Report;
                summary = result.Summary;
                spaceId = result.SpaceId;
                blockedReason = result.BlockedReason;
            }

            if (blockedReason is not null)
            {
                output.WriteLine($"Import blocked: {blockedReason}");
                return 2;
            }

            var formatted = ImportReportTextFormatter.Format(export.Space.Key, options.DryRun, report, summary);
            await File.WriteAllTextAsync(options.ReportPath, formatted, cancellationToken);

            output.WriteLine($"Report written to {options.ReportPath}");
            output.WriteLine(options.DryRun
                ? $"Dry run complete for space '{export.Space.Key}': {summary.PagesActuallyImported} of {summary.TotalPagesInExport} page(s) would import cleanly; {summary.SkippedPageCount} would be skipped."
                : $"Import complete. Space id: {spaceId}. {summary.PagesActuallyImported} of {summary.TotalPagesInExport} page(s) imported; {summary.SkippedPageCount} skipped.");

            return report.NeedsReview ? 3 : 0;
        }
    }
}
