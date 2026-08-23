using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Storage;

namespace RocketWiki.Sync.Cli;

/// <summary>
/// The console tool's actual logic, separated from <c>Program.cs</c> so tests can drive
/// it with an injectable <see cref="TextWriter"/> and an injectable DbContext factory —
/// same convention as <c>RocketWiki.Importer.Cli.ImporterCli</c>. The factory seam is
/// what lets RocketWiki.Sync.Tests run the ENTIRE CLI path (argument parsing → verb →
/// BundleExportService/BundleImportService → real bundle files on disk) against SQLite,
/// the §14 integration tier; the default factory targets SQL Server, and — same standing
/// caveat as ImporterCli — that default path has not been run against a real SQL Server
/// in this environment.
///
/// All bundle mechanics (numbering, manifest hash chain, gap refusal, idempotency,
/// shadow-user enrichment, blob packing) live in the two RocketWiki.Data services; this
/// class adds nothing to the format and never opens a bundle itself except to hand the
/// path over.
/// </summary>
public static class SyncCli
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? output = null,
        Func<string, RocketWikiDbContext>? dbContextFactory = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        dbContextFactory ??= CreateSqlServerContext;

        var options = SyncCliArgumentParser.Parse(args);
        if (options is null)
        {
            output.WriteLine(SyncCliArgumentParser.Usage);
            return 1;
        }

        var fileStorage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
        {
            FileSystem = new FileSystemFileStorageOptions { Root = options.AttachmentsRoot },
        }));

        await using var db = dbContextFactory(options.ConnectionString);

        return options.Verb switch
        {
            SyncVerb.Export => await RunExportAsync(db, fileStorage, options, output, cancellationToken),
            SyncVerb.Import => await RunImportAsync(db, fileStorage, options, output, cancellationToken),
            _ => 1,
        };
    }

    private static async Task<int> RunExportAsync(
        RocketWikiDbContext db, IFileStorage fileStorage, SyncCliOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var exportService = new BundleExportService(db, fileStorage);

        if (options.BaselineSpaceKey is not null)
        {
            // IgnoreQueryFilters: an archived space can still legitimately need its
            // baseline (exported-ness and archival are independent); the checks below
            // are about sync ownership, not visibility.
            var space = await db.Spaces.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Key == options.BaselineSpaceKey, cancellationToken);
            if (space is null)
            {
                output.WriteLine($"No space with key '{options.BaselineSpaceKey}' exists on this instance.");
                return 2;
            }

            if (!space.IsExported)
            {
                output.WriteLine(
                    $"Space '{space.Key}' is not flagged exported. Flag it exported first - a baseline for a " +
                    "non-exported space would start a sync stream nothing will ever continue.");
                return 2;
            }

            // design.md §12: "Only a native space can be exported; a replica must never
            // emit sync events for content it doesn't own." Enforced here rather than
            // assumed from IsExported alone - fail closed.
            if (!string.Equals(space.OriginInstanceId, options.InstanceId, StringComparison.Ordinal))
            {
                output.WriteLine(
                    $"Space '{space.Key}' originates from instance '{space.OriginInstanceId}', not this instance " +
                    $"('{options.InstanceId}'). A replica must never emit sync content it doesn't own - refusing.");
                return 2;
            }

            var baseline = await exportService.ExportBaselineAsync(space.Id, options.OutputDirectory!, options.InstanceId!, cancellationToken);
            output.WriteLine(
                $"Baseline bundle {baseline.BundleNumber} written to {baseline.BundleFilePath} " +
                $"({baseline.EventCount} event(s) for space '{space.Key}').");
            return 0;
        }

        var incremental = await exportService.ExportIncrementalAsync(options.OutputDirectory!, options.InstanceId!, cancellationToken);
        if (incremental is null)
        {
            output.WriteLine("Nothing pending in the sync outbox; no bundle written.");
            return 0;
        }

        output.WriteLine(
            $"Bundle {incremental.BundleNumber} written to {incremental.BundleFilePath} ({incremental.EventCount} event(s)).");
        return 0;
    }

    private static async Task<int> RunImportAsync(
        RocketWikiDbContext db, IFileStorage fileStorage, SyncCliOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        List<string> bundleFiles;
        if (Directory.Exists(options.BundlePath))
        {
            // Directory mode: apply everything present in bundle-number order (the
            // import service itself re-verifies order/gaps/chain from each manifest's
            // own declared number - the filename sort is a courtesy, not the check).
            bundleFiles = Directory.GetFiles(options.BundlePath!, "bundle-*.zip")
                .OrderBy(ParseBundleNumberFromFileName)
                .ThenBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (bundleFiles.Count == 0)
            {
                output.WriteLine($"No bundle-*.zip files found in '{options.BundlePath}'; nothing to import.");
                return 0;
            }
        }
        else if (File.Exists(options.BundlePath))
        {
            bundleFiles = [options.BundlePath!];
        }
        else
        {
            output.WriteLine($"'{options.BundlePath}' is neither a bundle file nor a directory.");
            return 1;
        }

        var importService = new BundleImportService(db, fileStorage);

        foreach (var bundleFile in bundleFiles)
        {
            var auditContext = new AuditContext(
                AuditChannel.Sync,
                $"sync-import:{Path.GetFileName(bundleFile)}:{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}",
                "cli");

            PageMutationResult<ImportedBundleSummary> result;
            try
            {
                result = await importService.ImportAsync(bundleFile, options.OriginInstanceId!, auditContext, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                // An unopenable zip or a structurally broken manifest is the same class
                // of loud, refuse-don't-absorb failure as a chain break (design.md §12).
                output.WriteLine($"REFUSED {Path.GetFileName(bundleFile)}: {ex.Message}");
                return 2;
            }

            if (!result.IsSuccess)
            {
                output.WriteLine($"REFUSED {Path.GetFileName(bundleFile)}: {DescribeRefusal(result.Error)}");
                return 2;
            }

            output.WriteLine(result.Value.WasDuplicate
                ? $"Skipped {Path.GetFileName(bundleFile)}: bundle {result.Value.BundleNumber} already applied (idempotent no-op)."
                : $"Applied {Path.GetFileName(bundleFile)}: bundle {result.Value.BundleNumber}, {result.Value.EventsApplied} event(s).");
        }

        return 0;
    }

    private static string DescribeRefusal(PageMutationError error) => error switch
    {
        BundleGapError e =>
            $"expected bundle {e.ExpectedBundleNumber} next but this bundle declares {e.ActualBundleNumber} - " +
            "import the missing bundle(s) first (design.md §12: bundles apply strictly in order).",
        BundleChainMismatchError e => $"manifest hash chain break - {e.Reason}",
        BundlePayloadTamperedError e => $"payload hash mismatch - {e.Reason}",
        SpaceSequenceGapError e =>
            $"per-space sequence gap in space {e.SpaceId}: expected sequence {e.ExpectedSequence}, got {e.ActualSequence}.",
        _ => error.ToString(),
    };

    private static int ParseBundleNumberFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("bundle-", StringComparison.Ordinal) && int.TryParse(name["bundle-".Length..], out var number)
            ? number
            : int.MaxValue;
    }

    private static RocketWikiDbContext CreateSqlServerContext(string connectionString)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RocketWikiDbContext>().UseSqlServer(connectionString);
        return new RocketWikiDbContext(optionsBuilder.Options);
    }
}
