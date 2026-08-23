using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Sync.Cli;
using Xunit;

namespace RocketWiki.Sync.Tests;

/// <summary>
/// design.md §12 Operations / §14 integration tier: the WHOLE RocketWiki.Sync CLI path
/// (argument parsing → verb dispatch → BundleExportService/BundleImportService → real
/// bundle zip files in a real temp directory) driven end-to-end between two genuinely
/// separate SQLite databases standing in for the low and high instances, through
/// <see cref="SyncCli.RunAsync"/>'s injectable DbContext-factory seam. The bundle
/// FORMAT mechanics (chain hashing, gap detection, idempotency, event application) are
/// exhaustively covered by RocketWiki.Data.Tests.BundleExportImportTests; what this
/// file proves is the operator-facing shell: verbs, flags, exit codes, refusal
/// messages, and the baseline-ownership checks the CLI itself owns.
/// </summary>
public sealed class SyncCliTests : IDisposable
{
    private const string LowInstanceId = "low-instance";

    private readonly SqliteConnection _lowConnection;
    private readonly SqliteConnection _highConnection;
    private readonly string _tempRoot;

    private string BundleDir => Path.Combine(_tempRoot, "bundles");
    private string LowStorageRoot => Path.Combine(_tempRoot, "low-storage");
    private string HighStorageRoot => Path.Combine(_tempRoot, "high-storage");

    public SyncCliTests()
    {
        _lowConnection = OpenDatabase();
        _highConnection = OpenDatabase();
        _tempRoot = Path.Combine(Path.GetTempPath(), "rocketwiki-sync-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BundleDir);
        Directory.CreateDirectory(LowStorageRoot);
        Directory.CreateDirectory(HighStorageRoot);
    }

    public void Dispose()
    {
        _lowConnection.Dispose();
        _highConnection.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<RocketWikiDbContext>().UseSqlite(connection).Options;
        using var context = new RocketWikiDbContext(options);
        context.Database.EnsureCreated();
        return connection;
    }

    /// <summary>The CLI's --connection-string argument selects which database a run targets: "low" or "high".</summary>
    private RocketWikiDbContext CreateContext(string connectionString)
    {
        var (connection, localInstanceId) = connectionString switch
        {
            // Each scratch database belongs to its own instance (design.md §12);
            // declaring it on the context lets the outbox writer verify ownership
            // when a test seeds outbox rows through the real services. The high
            // side never journals (imports raise no sync-relevant events), but its
            // identity is declared anyway - honesty over minimality.
            "low" => (_lowConnection, LowInstanceId),
            "high" => (_highConnection, "high-instance"),
            _ => throw new InvalidOperationException($"Test factory got unexpected connection string '{connectionString}'."),
        };

        var options = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlite(connection).UseLocalInstanceId(localInstanceId).Options;
        return new RocketWikiDbContext(options);
    }

    private async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        using var output = new StringWriter();
        var exitCode = await SyncCli.RunAsync(args, output, CreateContext);
        return (exitCode, output.ToString());
    }

    // --- Seeding (mirrors RocketWiki.Data.Tests.TestData, which this project can't reference) ---

    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "cli-test-req", "127.0.0.1");

    private static Principal EditorPrincipal() => Principal.Create("editor-sub", []);

    private RocketWikiDbContext SeedLowSpace(out User actor, out Space space, bool isExported = true, string? originInstanceId = null)
    {
        actor = new User
        {
            DisplayName = "Low Author",
            Subject = $"sub-{Guid.NewGuid():N}",
            AttributesJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        space = new Space
        {
            Key = "ENG",
            Name = "ENG Space",
            OriginInstanceId = originInstanceId ?? LowInstanceId,
            IsExported = isExported,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
        };

        var context = CreateContext("low");
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });
        context.SaveChanges();
        return context;
    }

    // --- Usage / input errors ------------------------------------------------------------

    [Fact]
    public async Task NoArguments_PrintsUsage_Exit1()
    {
        var (exitCode, output) = await RunCliAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("RocketWiki.Sync", output);
        Assert.Contains("export", output);
        Assert.Contains("import", output);
    }

    [Fact]
    public async Task UnknownVerbOrMissingFlags_PrintsUsage_Exit1()
    {
        var (unknownVerb, _) = await RunCliAsync("frobnicate", "--connection-string", "low");
        Assert.Equal(1, unknownVerb);

        // export missing --output / --instance-id
        var (incompleteExport, _) = await RunCliAsync("export", "--connection-string", "low", "--attachments-root", LowStorageRoot);
        Assert.Equal(1, incompleteExport);

        // import missing --origin-instance-id
        var (incompleteImport, _) = await RunCliAsync(
            "import", "--connection-string", "high", "--attachments-root", HighStorageRoot, "--bundle", BundleDir);
        Assert.Equal(1, incompleteImport);
    }

    [Fact]
    public async Task Import_NonexistentBundlePath_Exit1()
    {
        var (exitCode, output) = await RunCliAsync(
            "import", "--connection-string", "high", "--attachments-root", HighStorageRoot,
            "--bundle", Path.Combine(_tempRoot, "no-such-thing"), "--origin-instance-id", LowInstanceId);

        Assert.Equal(1, exitCode);
        Assert.Contains("neither a bundle file nor a directory", output);
    }

    // --- Baseline ownership checks (the CLI's own responsibility, design.md §12) ---------

    [Fact]
    public async Task Baseline_UnknownSpaceKey_Refused_Exit2()
    {
        using var low = SeedLowSpace(out _, out _);

        var (exitCode, output) = await RunExportAsync("--baseline", "NOPE");

        Assert.Equal(2, exitCode);
        Assert.Contains("No space with key 'NOPE'", output);
    }

    [Fact]
    public async Task Baseline_OnNonExportedSpace_Refused_Exit2()
    {
        using var low = SeedLowSpace(out _, out _, isExported: false);

        var (exitCode, output) = await RunExportAsync("--baseline", "ENG");

        Assert.Equal(2, exitCode);
        Assert.Contains("not flagged exported", output);
    }

    [Fact]
    public async Task Baseline_OnReplicaSpace_Refused_Exit2()
    {
        // A space this instance does NOT own, however it came to be flagged exported:
        // "a replica must never emit sync events for content it doesn't own" (§12).
        using var low = SeedLowSpace(out _, out _, isExported: true, originInstanceId: "someone-else");

        var (exitCode, output) = await RunExportAsync("--baseline", "ENG");

        Assert.Equal(2, exitCode);
        Assert.Contains("must never emit sync content it doesn't own", output);
        Assert.Empty(Directory.GetFiles(BundleDir));
    }

    // --- The full round trip through real temp files -------------------------------------

    [Fact]
    public async Task BaselineThenIncremental_ExportedAndImportedThroughCli_RoundTripsToHigh()
    {
        using (var low = SeedLowSpace(out var actor, out var space))
        {
            // A pre-existing page: only the baseline can carry it (no outbox event).
            low.Pages.Add(new Page
            {
                SpaceId = space.Id,
                AncestorPath = "/",
                Slug = "home",
                Title = "Home",
                CurrentContent = "# Welcome",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            low.SaveChanges();
        }

        var (baselineExit, baselineOutput) = await RunExportAsync("--baseline", "ENG");
        Assert.Equal(0, baselineExit);
        Assert.Contains("Baseline bundle 1", baselineOutput);
        Assert.True(File.Exists(Path.Combine(BundleDir, "bundle-000001.zip")));

        // A post-baseline change, via the real service so it journals to the outbox.
        Guid secondPageId;
        using (var low = CreateContext("low"))
        {
            var actorId = low.Users.Single().Id;
            var pageService = new PageService(low, LowInstanceId);
            var created = await pageService.CreatePageAsync(
                new CreatePageRequest(low.Spaces.Single().Id, null, "second", "Second", "# Second"),
                EditorPrincipal(), actorId, AuditCtx);
            Assert.True(created.IsSuccess);
            secondPageId = created.Value.Id;
        }

        var (incrementalExit, incrementalOutput) = await RunExportAsync();
        Assert.Equal(0, incrementalExit);
        Assert.Contains("Bundle 2", incrementalOutput);

        // Import the whole directory on high - both bundles, in order, one command.
        var (importExit, importOutput) = await RunImportAsync(BundleDir);
        Assert.Equal(0, importExit);
        Assert.Contains("Applied bundle-000001.zip: bundle 1", importOutput);
        Assert.Contains("Applied bundle-000002.zip: bundle 2", importOutput);

        using (var high = CreateContext("high"))
        {
            Assert.Equal(2, high.Pages.IgnoreQueryFilters().Count());
            Assert.Single(high.Pages, p => p.Id == secondPageId && p.Title == "Second");
            var importState = high.SyncImportStates.Single();
            Assert.Equal(LowInstanceId, importState.OriginInstanceId);
            Assert.Equal(2, importState.LastBundleNumber);
            Assert.Contains(high.AuditEvents, e => e.Action == "sync.import");
        }

        // Re-running the same import is an idempotent no-op, not an error (design.md §12).
        var (rerunExit, rerunOutput) = await RunImportAsync(BundleDir);
        Assert.Equal(0, rerunExit);
        Assert.Contains("already applied", rerunOutput);
        using (var high = CreateContext("high"))
        {
            Assert.Equal(2, high.Pages.IgnoreQueryFilters().Count()); // not duplicated
        }
    }

    [Fact]
    public async Task ExportIncremental_NothingPending_Exit0_WritesNoBundle()
    {
        using var low = SeedLowSpace(out _, out _);

        var (exitCode, output) = await RunExportAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("Nothing pending", output);
        Assert.Empty(Directory.GetFiles(BundleDir));
    }

    // --- Chain tampering fails loudly through the CLI ------------------------------------

    [Fact]
    public async Task Import_TamperedManifestChain_RefusedLoudly_Exit2_NothingFromThatBundleLands()
    {
        using (var low = SeedLowSpace(out var actor, out var space))
        {
            low.Pages.Add(new Page
            {
                SpaceId = space.Id,
                AncestorPath = "/",
                Slug = "home",
                Title = "Home",
                CurrentContent = "# Welcome",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            low.SaveChanges();
        }

        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        Guid tamperedPageId;
        using (var low = CreateContext("low"))
        {
            var pageService = new PageService(low, LowInstanceId);
            var created = await pageService.CreatePageAsync(
                new CreatePageRequest(low.Spaces.Single().Id, null, "second", "Second", "# Second"),
                EditorPrincipal(), low.Users.Single().Id, AuditCtx);
            Assert.True(created.IsSuccess);
            tamperedPageId = created.Value.Id;
        }

        Assert.Equal(0, (await RunExportAsync()).ExitCode);

        // Break the chain: rewrite bundle 2's previousManifestHash in place, leaving its
        // bundle number and payload hash intact so ONLY the chain check can catch it.
        var bundle2Path = Path.Combine(BundleDir, "bundle-000002.zip");
        using (var archive = ZipFile.Open(bundle2Path, ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry("manifest.json")!;
            JsonObject manifestJson;
            using (var readStream = manifestEntry.Open())
            {
                manifestJson = JsonNode.Parse(readStream)!.AsObject();
            }

            manifestJson["previousManifestHash"] = new string('f', 64);
            manifestEntry.Delete();
            var rewritten = archive.CreateEntry("manifest.json");
            await using var writer = new StreamWriter(rewritten.Open());
            await writer.WriteAsync(manifestJson.ToJsonString());
        }

        var (exitCode, output) = await RunImportAsync(BundleDir);

        Assert.Equal(2, exitCode);
        Assert.Contains("Applied bundle-000001.zip", output); // the intact bundle landed first
        Assert.Contains("REFUSED bundle-000002.zip", output);
        Assert.Contains("hash chain break", output);

        using var high = CreateContext("high");
        Assert.Empty(high.Pages.IgnoreQueryFilters().Where(p => p.Id == tamperedPageId)); // nothing from bundle 2
        Assert.Equal(1, high.SyncImportStates.Single().LastBundleNumber); // position did not advance

        // The refusal itself left a durable record (design.md §12/§7) - written on a
        // FRESH context, so recording it flushed nothing bundle 2 partially applied
        // (the two assertions above prove that). NOT sync.import + Denied: §7's Denied
        // is an access-control outcome (a principal, a failing restriction); an
        // integrity refusal has neither, so it is its own action, recorded as the
        // Success it operationally is - see DomainEventAuditMapper.
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Equal(AuditOutcome.Success, refusal.Outcome);
        Assert.Equal(AuditChannel.Sync, refusal.Channel);
        Assert.Null(refusal.UserId); // system action, like sync.import itself
        Assert.Contains("chain_mismatch", refusal.DetailsJson);
        Assert.Contains("bundle-000002.zip", refusal.DetailsJson);
        Assert.Contains(LowInstanceId, refusal.DetailsJson);

        // And the applied bundle's own audit row is unchanged by any of this.
        Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import").ToList());
    }

    [Fact]
    public async Task Import_UnreadableBundleFile_RefusedLoudly_Exit2_WithAuditRow()
    {
        // Not a zip at all - the same refuse-don't-absorb class as a chain break.
        var garbagePath = Path.Combine(BundleDir, "bundle-000001.zip");
        await File.WriteAllTextAsync(garbagePath, "this is not a zip archive");

        var (exitCode, output) = await RunImportAsync(garbagePath);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);

        using var high = CreateContext("high");
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Equal(AuditOutcome.Success, refusal.Outcome);
        Assert.Equal(AuditChannel.Sync, refusal.Channel);
        Assert.Contains("unreadable", refusal.DetailsJson);
        Assert.Contains("bundle-000001.zip", refusal.DetailsJson);
        Assert.Empty(high.SyncImportStates.ToList()); // nothing advanced, nothing landed
    }

    // --- Helpers -------------------------------------------------------------------------

    private Task<(int ExitCode, string Output)> RunExportAsync(params string[] extraArgs) =>
        RunCliAsync([
            "export",
            "--connection-string", "low",
            "--output", BundleDir,
            "--instance-id", LowInstanceId,
            "--attachments-root", LowStorageRoot,
            .. extraArgs,
        ]);

    private Task<(int ExitCode, string Output)> RunImportAsync(string bundlePath) =>
        RunCliAsync(
            "import",
            "--connection-string", "high",
            "--bundle", bundlePath,
            "--origin-instance-id", LowInstanceId,
            "--attachments-root", HighStorageRoot);
}
