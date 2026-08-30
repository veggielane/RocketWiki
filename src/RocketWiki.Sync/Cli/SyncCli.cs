using System.Text.Json;
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
            SyncVerb.Import => await RunImportAsync(db, fileStorage, options, dbContextFactory, output, cancellationToken),
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
                    $"Space '{space.Key}' is not flagged exported. Flag it with the setSpaceExported mutation " +
                    "(instance admin) first - a baseline for a non-exported space would start a sync stream " +
                    "nothing will ever continue, because the outbox only journals exported spaces.");
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
        RocketWikiDbContext db, IFileStorage fileStorage, SyncCliOptions options,
        Func<string, RocketWikiDbContext> dbContextFactory, TextWriter output, CancellationToken cancellationToken)
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

        // The local instance id is what makes the self-origin refusal possible; without
        // it an instance cannot tell its own content coming back from a real replica.
        var importService = new BundleImportService(db, fileStorage, options.InstanceId);

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
            catch (Exception ex) when (IsMalformedBundle(ex))
            {
                // An unopenable zip or a structurally broken manifest is the same class
                // of loud, refuse-don't-absorb failure as a chain break (design.md §12).
                var detail = $"REFUSED {Path.GetFileName(bundleFile)}: {ex.Message}";
                output.WriteLine(detail);
                await RecordRefusalAsync(
                    dbContextFactory, options, auditContext, bundleFile, "unreadable", detail, declaredBundleNumber: null, cancellationToken);
                return 2;
            }

            if (!result.IsSuccess)
            {
                var detail = $"REFUSED {Path.GetFileName(bundleFile)}: {DescribeRefusal(result.Error)}";
                output.WriteLine(detail);
                await RecordRefusalAsync(
                    dbContextFactory, options, auditContext, bundleFile, ClassifyRefusal(result.Error), detail,
                    declaredBundleNumber: result.Error is BundleGapError gap ? gap.ActualBundleNumber : null, cancellationToken);
                return 2;
            }

            output.WriteLine(result.Value.WasDuplicate
                ? $"Skipped {Path.GetFileName(bundleFile)}: bundle {result.Value.BundleNumber} already applied (idempotent no-op)."
                : $"Applied {Path.GetFileName(bundleFile)}: bundle {result.Value.BundleNumber}, {result.Value.EventsApplied} event(s).");
        }

        return 0;
    }

    /// <summary>
    /// design.md §12/§7: an integrity refusal must leave a durable record, not only an
    /// exit code - it is exactly the "detected error, never a silent absorb" the hash
    /// chain exists to produce, and a paging job's evidence trail. Written through the
    /// same domain-event → audit seam every Data-layer action uses (sync.import.refused,
    /// AuditChannel.Sync - see DomainEventAuditMapper for why this is NOT sync.import +
    /// Denied), but on a FRESH DbContext from the factory: the import context that just
    /// refused still tracks whatever the bundle partially applied before its integrity
    /// check failed, and calling SaveChanges on IT would flush exactly the content the
    /// refusal exists to keep out. A failure writing this row propagates - a refusal
    /// that cannot be recorded fails loudly (nonzero exit), never silently (§7:
    /// fail-closed applies to observability too).
    /// </summary>
    private static async Task RecordRefusalAsync(
        Func<string, RocketWikiDbContext> dbContextFactory, SyncCliOptions options, AuditContext auditContext,
        string bundleFile, string reason, string detail, int? declaredBundleNumber, CancellationToken cancellationToken)
    {
        await using var auditDb = dbContextFactory(options.ConnectionString);
        auditDb.AuditContext = auditContext;
        auditDb.RaiseDomainEvent(new SyncImportRefusedEvent(
            options.OriginInstanceId!, Path.GetFileName(bundleFile), declaredBundleNumber, reason, detail));
        await auditDb.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// "The bundle's own bytes are malformed", as an exception filter.
    ///
    /// <para>This used to read <c>InvalidDataException or InvalidOperationException</c>,
    /// which covered an unopenable zip and a missing entry and nothing else — so a bundle
    /// with a truncated JSON line, a required property missing, or an unparseable date
    /// <b>crashed the CLI</b> instead of being refused. That is an audit gap on the
    /// boundary, and precisely the one §7 exists to close: the operator got a stack trace
    /// and an exit code of 134 where the contract promises exit 2 and a durable
    /// <c>sync.import.refused</c> row naming the file. The refusal machinery was fine; it
    /// simply was not reached.</para>
    ///
    /// <para><b>Widened to a named list, not to <c>Exception</c>.</b> Each entry below
    /// corresponds to a throw site reachable from bundle content:</para>
    /// <list type="bullet">
    /// <item><see cref="InvalidDataException"/> — a corrupt zip, and the import's own
    /// "this bundle says something impossible" refusals.</item>
    /// <item><see cref="InvalidOperationException"/> — a missing archive entry, an
    /// unparseable manifest, a <c>JsonElement</c> read as the wrong kind.</item>
    /// <item><see cref="JsonException"/> — a malformed manifest or NDJSON line.</item>
    /// <item><see cref="KeyNotFoundException"/> — <c>GetProperty</c> for a required
    /// payload key the bundle does not carry.</item>
    /// <item><see cref="FormatException"/> / <see cref="OverflowException"/> — a value
    /// that is the right kind but not a legal guid, date, or number.</item>
    /// </list>
    ///
    /// <para><b>What is deliberately NOT caught matters as much.</b> A database failure, a
    /// disk fault, or a cancellation is not the bundle being malformed, and recording one
    /// as <c>reason: unreadable</c> against this file would put a false statement in the
    /// regulated record — worse than the crash, because it looks like evidence. Those
    /// still propagate.</para>
    /// </summary>
    private static bool IsMalformedBundle(Exception ex) =>
        ex is InvalidDataException
            or InvalidOperationException
            or JsonException
            or KeyNotFoundException
            or FormatException
            or OverflowException;

    private static string ClassifyRefusal(PageMutationError error) => error switch
    {
        BundleGapError => "bundle_gap",
        BundleChainMismatchError => "chain_mismatch",
        BundlePayloadTamperedError => "payload_hash_mismatch",
        BundleBlobTamperedError => "blob_hash_mismatch",
        BundleTooLargeError => "size_limit_exceeded",
        BundleOriginMismatchError => "origin_mismatch",
        SpaceSequenceGapError => "space_sequence_gap",
        BundleFormatUnsupportedError => "unsupported_format",
        _ => error.GetType().Name,
    };

    private static string DescribeRefusal(PageMutationError error) => error switch
    {
        BundleGapError e =>
            $"expected bundle {e.ExpectedBundleNumber} next but this bundle declares {e.ActualBundleNumber} - " +
            "import the missing bundle(s) first (design.md §12: bundles apply strictly in order).",
        BundleChainMismatchError e => $"manifest hash chain break - {e.Reason}",
        BundlePayloadTamperedError e => $"payload hash mismatch - {e.Reason}",
        // Same refusal family as the two above, and phrased the same way on purpose: an
        // attachment's bytes not matching the hash the (chain-covered) event line declares
        // for them is a tampered payload, just one that lives in its own archive entry.
        BundleBlobTamperedError e => $"attachment content hash mismatch - {e.Reason}",
        BundleTooLargeError e => $"bundle size limit exceeded - {e.Reason}",
        BundleOriginMismatchError e =>
            $"the bundle's manifest declares origin instance '{e.DeclaredInstanceId}', but --origin-instance-id " +
            $"says '{e.ExpectedInstanceId}'. Importing it under the wrong origin would splice two instances' " +
            "bundle streams into one position (design.md §12) - check the flag, or the bundle directory.",
        SpaceSequenceGapError e =>
            $"per-space sequence gap in space {e.SpaceId}: expected sequence {e.ExpectedSequence}, got {e.ActualSequence}.",
        BundleFormatUnsupportedError e =>
            $"bundle declares format version {e.BundleFormatVersion}, but this instance understands at most " +
            $"{e.MaxSupportedVersion} - a newer format is refused, never partially understood. Upgrade this " +
            "instance, then re-run the import.",
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
