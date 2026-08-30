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

        // Path overload, not the stream one: the export owns the file handle and closes it
        // when `using (export)` below disposes it. This used to open the FileStream here
        // under a `using var` INSIDE the try, which closed it at the end of that block —
        // while the export's lazy OpenContent closures still needed it. Pass 2 then threw
        // ObjectDisposedException on the first attachment, unhandled, after every page had
        // already been committed and before the report was written. Every real
        // (non-dry-run) import carrying an attachment died there.
        ConfluenceSpaceExport export;
        try
        {
            export = new ConfluenceXmlExportReader().Read(options.ExportPath);
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
                WarnOnWideGrant(options.InitialGrantExpressionJson!, export.Space.Key, output);

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

            // Written BEFORE the blocked check, not after. A blocked run is the run whose
            // report matters most: it says which pages were already committed before the
            // import stopped, and — even when the space itself could not be created — what
            // the Confluence export restricted (design.md §13). Returning early left an
            // operator with one line of console output and no artefact at all.
            var formatted = ImportReportTextFormatter.Format(export.Space.Key, options.DryRun, report, summary);
            await File.WriteAllTextAsync(options.ReportPath, formatted, cancellationToken);

            if (blockedReason is not null)
            {
                output.WriteLine($"Import blocked: {blockedReason}");
                output.WriteLine($"Report written to {options.ReportPath}");
                return 2;
            }

            output.WriteLine($"Report written to {options.ReportPath}");
            output.WriteLine(options.DryRun
                ? $"Dry run complete for space '{export.Space.Key}': {summary.PagesActuallyImported} of {summary.TotalPagesInExport} page(s) would import cleanly; {summary.SkippedPageCount} would be skipped."
                : $"Import complete. Space id: {spaceId}. {summary.PagesActuallyImported} of {summary.TotalPagesInExport} page(s) imported; {summary.SkippedPageCount} skipped.");

            return report.NeedsReview ? 3 : 0;
        }
    }

    /// <summary>
    /// Says out loud what <c>--grant-expression '{"everyone": true}'</c> means before the
    /// space is created with it.
    ///
    /// <para>The grant given here becomes the imported space's <b>initial space grant</b>
    /// (design.md §6): the rule deciding who may read everything the import is about to
    /// write. An <c>everyone</c> rule opens a whole migrated Confluence space — which on
    /// this product is export-controlled content by default — to every authenticated
    /// principal on the instance, in one flag, silently.</para>
    ///
    /// <para>It is a warning and not a refusal on purpose. Opening a space to everyone is a
    /// legitimate choice for genuinely general content, and this is an operator tool run
    /// deliberately; a hard block would only teach people to work around it. What was
    /// missing was not permission, it was that the choice was invisible — the flag went in
    /// and the space came out with no line anywhere saying what had just been granted.
    /// Clearance still applies underneath regardless (§6.3): a grant widens who may reach a
    /// space, never what they may read inside it.</para>
    ///
    /// <para>Matched on the parsed expression rather than the raw string so whitespace and
    /// key casing cannot slip past it. An expression that will not parse is left alone —
    /// the import's own grant preflight refuses that case with a better message.</para>
    ///
    /// <para><b>internal</b> rather than private so it is testable on its own: the path
    /// that calls it needs a live SQL Server two statements later, so a black-box test
    /// through <see cref="RunAsync"/> could never reach it.</para>
    /// </summary>
    internal static void WarnOnWideGrant(string grantExpressionJson, string spaceKey, TextWriter output)
    {
        bool isEveryone;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(grantExpressionJson);
            isEveryone = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("everyone", out var everyone)
                && everyone.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (!isEveryone)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("  !! WIDE GRANT !!");
        output.WriteLine(
            $"  --grant-expression is {{\"everyone\": true}}, so space '{spaceKey}' will be readable by EVERY");
        output.WriteLine(
            "  authenticated principal on this instance the moment it is created - including everything");
        output.WriteLine(
            "  this import is about to write into it. For a migrated Confluence space that is very often");
        output.WriteLine(
            "  wrong: prefer a group or attribute rule (e.g. {\"group\": \"engineering\"}) and widen later.");
        output.WriteLine(
            "  (Protective markings still apply underneath - a grant widens who may reach the space, never");
        output.WriteLine(
            "  what they may read inside it - but the space's existence, titles and structure are exposed.)");
        output.WriteLine();
    }
}
