using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Sync;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Storage;
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

    /// <summary>The importing (high) instance's own id. Required on `import` so the CLI can
    /// refuse a bundle that originates from here — see BundleSelfOriginError. Distinct from
    /// LowInstanceId, which is what the bundles under test declare as their origin.</summary>
    private const string HighInstanceId = "high-instance";

    /// <summary>Named from the parser rather than re-spelled, so the two cannot drift.</summary>
    private const string ConnectionStringVariable = SyncCliArgumentParser.ConnectionStringVariable;

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
        // Access beside the role (design.md 6.4: roles never supersede access) - the
        // editor who writes the pages below must be able to see the space at all.
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
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
            "--bundle", Path.Combine(_tempRoot, "no-such-thing"),
            "--instance-id", HighInstanceId, "--origin-instance-id", LowInstanceId);

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
    public async Task Import_FutureFormatBundle_RefusedLoudly_Exit2_WithAuditRow()
    {
        // A bundle from a NEWER era (BundleFormat): well-formed zip, parseable manifest,
        // but a formatVersion this instance doesn't understand. Same exit-2 +
        // sync.import.refused contract as a chain break - the scheduled job pages, the
        // operator upgrades, nothing is partially understood in the meantime. (The
        // mirror case - THIS importer being the old one refusing a format it predates -
        // is what the per-format events entry name exists for: it lands in the
        // unreadable-refusal path tested above, by construction rather than by code.)
        var bundlePath = Path.Combine(BundleDir, "bundle-000001.zip");
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            instanceId = LowInstanceId,
            bundleNumber = 1,
            previousManifestHash = (string?)null,
            payloadSha256 = new string('0', 64),
            spaceEventRanges = new Dictionary<string, object>(),
            formatVersion = 99,
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using (var fileStream = new FileStream(bundlePath, FileMode.CreateNew))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
        {
            writer.Write(manifestJson);
        }

        var (exitCode, output) = await RunImportAsync(bundlePath);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);
        Assert.Contains("format version 99", output);
        Assert.Contains("Upgrade this", output);

        using var high = CreateContext("high");
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Equal(AuditOutcome.Success, refusal.Outcome);
        Assert.Equal(AuditChannel.Sync, refusal.Channel);
        Assert.Contains("unsupported_format", refusal.DetailsJson);
        Assert.Contains("bundle-000001.zip", refusal.DetailsJson);
        Assert.Empty(high.SyncImportStates.ToList()); // nothing advanced, nothing landed
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

    // --- Attachment bytes crossing the boundary (design.md §12) ---------------------------

    /// <summary>
    /// <b>The courier test.</b> A bundle is exported with an attachment, its file content
    /// is replaced on the "transfer medium" while its entry name (and therefore the content
    /// hash the bundle declares for it) is left alone, and the import refuses.
    ///
    /// <para>This is the shape of the finding it closes. The manifest hash chain and
    /// <c>PayloadSha256</c> cover the events file; the attachment BINARIES sit in their own
    /// <c>blobs/</c> entries outside both. The importer streamed those bytes straight into
    /// high-side storage and set <c>Attachment.ContentHash</c> from the hash the bundle
    /// DECLARED — never from the bytes — so a file could be substituted anywhere on the
    /// medium with nothing detecting it, on the one boundary whose entire purpose is that
    /// only vetted content crosses. Everything else about this bundle still verifies: only
    /// a blob-level check can catch it.</para>
    ///
    /// <para>Exit 2 and a <c>sync.import.refused</c> row, exactly like a chain break — an
    /// integrity refusal has to leave evidence, not just a nonzero exit (§7).</para>
    /// </summary>
    [Fact]
    public async Task Import_TamperedAttachmentBlob_RefusedLoudly_Exit2_NothingLands()
    {
        var pageId = await SeedPageWithAttachmentAsync("the original diagram bytes"u8.ToArray());
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var bundlePath = Path.Combine(BundleDir, "bundle-000001.zip");
        using (var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update))
        {
            var blob = Assert.Single(
                archive.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.Ordinal)).ToList());
            var name = blob.FullName;
            blob.Delete();
            await using var stream = archive.CreateEntry(name).Open();
            await stream.WriteAsync("substituted - an entirely different file"u8.ToArray());
        }

        var (exitCode, output) = await RunImportAsync(bundlePath);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);
        Assert.Contains("attachment content hash mismatch", output);

        using var high = CreateContext("high");
        // Nothing landed - not the attachment, not the page that carried it, not the
        // import position. And no blob in high-side storage either: the check is a
        // pre-pass precisely because a partial write there would be permanent (the rows
        // evaporate with the unsaved change set, the bytes do not).
        Assert.Empty(high.Attachments.IgnoreQueryFilters().ToList());
        Assert.Empty(high.Pages.IgnoreQueryFilters().Where(p => p.Id == pageId).ToList());
        Assert.Empty(high.SyncImportStates.ToList());
        Assert.Empty(Directory.GetFiles(HighStorageRoot, "*", SearchOption.AllDirectories));

        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Equal(AuditOutcome.Success, refusal.Outcome);
        Assert.Equal(AuditChannel.Sync, refusal.Channel);
        Assert.Contains("blob_hash_mismatch", refusal.DetailsJson);
        Assert.Contains("bundle-000001.zip", refusal.DetailsJson);
    }

    /// <summary>The control the tamper test needs to mean anything: the same bundle,
    /// untouched, crosses and puts byte-identical content in high-side storage.</summary>
    [Fact]
    public async Task Import_UntamperedAttachmentBlob_LandsOnHigh_ByteIdentical()
    {
        var originalBytes = "the original diagram bytes"u8.ToArray();
        await SeedPageWithAttachmentAsync(originalBytes);
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var (exitCode, output) = await RunImportAsync(BundleDir);

        Assert.Equal(0, exitCode);
        Assert.Contains("Applied bundle-000001.zip", output);

        using var high = CreateContext("high");
        var landed = Assert.Single(high.Attachments.IgnoreQueryFilters().ToList());
        Assert.Equal(SHA256.HashData(originalBytes), landed.ContentHash);

        var highStorage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
        {
            FileSystem = new FileSystemFileStorageOptions { Root = HighStorageRoot },
        }));
        await using var readBack = await highStorage.OpenReadAsync(landed.StorageKey, CancellationToken.None);
        using var buffer = new MemoryStream();
        await readBack.CopyToAsync(buffer);
        Assert.Equal(originalBytes, buffer.ToArray());
    }

    // --- Malformed and oversized bundles are refusals, not crashes ------------------------

    /// <summary>
    /// A structurally-broken manifest. The CLI's catch filter used to be
    /// <c>InvalidDataException or InvalidOperationException</c>, which covers an unopenable
    /// zip and a missing entry and nothing else — so truncated JSON escaped as a
    /// <c>JsonException</c> and <b>crashed the tool</b>. That is an audit gap on the
    /// boundary: the contract promises exit 2 and a durable row naming the file, and a
    /// corrupt bundle instead left a stack trace and no evidence at all. The refusal
    /// machinery was never broken; it simply was not reached.
    ///
    /// <para>Mutation-tested: narrow the filter back to the two original types and this
    /// test fails with the JsonException escaping <c>SyncCli.RunAsync</c>.</para>
    /// </summary>
    [Fact]
    public async Task Import_MalformedManifestJson_RefusedLoudly_Exit2_WithAuditRow()
    {
        await SeedPageWithAttachmentAsync("bytes"u8.ToArray());
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var bundlePath = Path.Combine(BundleDir, "bundle-000001.zip");
        using (var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update))
        {
            archive.GetEntry("manifest.json")!.Delete();
            await using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            await writer.WriteAsync("{\"instanceId\":\"low-instance\",\"bundleNum");
        }

        var (exitCode, output) = await RunImportAsync(bundlePath);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);

        using var high = CreateContext("high");
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Equal(AuditChannel.Sync, refusal.Channel);
        Assert.Contains("unreadable", refusal.DetailsJson);
        Assert.Contains("bundle-000001.zip", refusal.DetailsJson);
        Assert.Empty(high.SyncImportStates.ToList());
    }

    /// <summary>
    /// An event payload missing a required property — <c>GetProperty</c>'s
    /// <see cref="KeyNotFoundException"/>, the other family that escaped the old filter.
    /// The payload hash is re-stamped so this reaches the apply path rather than being
    /// refused earlier as a tampered payload; the point is what happens to a bundle whose
    /// bytes are internally consistent and whose CONTENT this build cannot make sense of.
    /// </summary>
    [Fact]
    public async Task Import_EventPayloadMissingARequiredProperty_RefusedLoudly_Exit2_WithAuditRow()
    {
        await SeedPageWithAttachmentAsync("bytes"u8.ToArray());
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var bundlePath = Path.Combine(BundleDir, "bundle-000001.zip");
        RewriteEventsAndRestampHash(bundlePath, events => events.Replace("ancestorPath", "ancestorPathX", StringComparison.Ordinal));

        var (exitCode, output) = await RunImportAsync(bundlePath);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);

        using var high = CreateContext("high");
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Contains("unreadable", refusal.DetailsJson);
        Assert.Empty(high.Pages.IgnoreQueryFilters().ToList());
        Assert.Empty(high.SyncImportStates.ToList());
    }

    /// <summary>
    /// The decompression ceiling, exercised through the CLI at its PRODUCTION limits
    /// (<see cref="BundleLimits.Default"/>) rather than an injected small one — a manifest
    /// of nine megabytes, which compresses to a few kilobytes on the medium. Refused before
    /// it is parsed, because every integrity check the format has happens after something
    /// has already been decompressed.
    /// </summary>
    [Fact]
    public async Task Import_ManifestOverTheSizeCeiling_RefusedLoudly_Exit2_WithAuditRow()
    {
        var bundlePath = Path.Combine(BundleDir, "bundle-000001.zip");
        using (var fileStream = new FileStream(bundlePath, FileMode.CreateNew))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
        {
            // Valid JSON, and nine mebibytes of it - so the refusal is about size, not
            // about the parse failing on garbage.
            await writer.WriteAsync("{\"instanceId\":\"");
            for (var i = 0; i < 9 * 1024; i++)
            {
                await writer.WriteAsync(new string('a', 1024));
            }

            await writer.WriteAsync("\",\"bundleNumber\":1}");
        }

        var (exitCode, output) = await RunImportAsync(bundlePath);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);
        Assert.Contains("size limit exceeded", output);

        using var high = CreateContext("high");
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Equal(AuditChannel.Sync, refusal.Channel);
        Assert.Contains("size_limit_exceeded", refusal.DetailsJson);
        Assert.Empty(high.SyncImportStates.ToList());
    }

    /// <summary>
    /// The operator flag and the bundle's own declaration have to agree. Until they were
    /// compared, <c>manifest.InstanceId</c> was written by every exporter and read by
    /// nothing: a bundle from instance A imported with <c>--origin-instance-id B</c> landed
    /// under B's stream position, splicing two instances' bundle numbers and hash chains
    /// into one. A wrong flag on a scheduled job is the realistic route in.
    /// </summary>
    [Fact]
    public async Task Import_BundleFromADifferentOrigin_RefusedLoudly_Exit2_WithAuditRow()
    {
        await SeedPageWithAttachmentAsync("bytes"u8.ToArray());
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var (exitCode, output) = await RunCliAsync(
            "import",
            "--connection-string", "high",
            "--bundle", BundleDir,
            "--instance-id", HighInstanceId,
            "--origin-instance-id", "a-different-low-instance",
            "--attachments-root", HighStorageRoot);

        Assert.Equal(2, exitCode);
        Assert.Contains("REFUSED bundle-000001.zip", output);
        Assert.Contains("declares origin instance", output);

        using var high = CreateContext("high");
        var refusal = Assert.Single(high.AuditEvents.Where(e => e.Action == "sync.import.refused").ToList());
        Assert.Contains("origin_mismatch", refusal.DetailsJson);
        Assert.Empty(high.Pages.IgnoreQueryFilters().ToList());
        Assert.Empty(high.SyncImportStates.ToList());
    }

    // --- The connection string does not have to go on the command line --------------------

    /// <summary>
    /// A connection string passed as <c>--connection-string</c> is in this process's ARGV,
    /// which every platform this runs on exposes to other local users (<c>ps</c>,
    /// <c>/proc/&lt;pid&gt;/cmdline</c>, Process Explorer) — and it lands in shell history
    /// and crash dumps besides. There was no alternative; now there are two, and the
    /// environment one needs no flag at all, which is what makes it the path of least
    /// resistance rather than the path of most virtue.
    ///
    /// <para>Driven end to end through the CLI rather than against the parser, because what
    /// matters is that a real run works with no secret in argv. The test factory maps the
    /// resolved value to a database, so an import that succeeds proves the value arrived.</para>
    ///
    /// <para>Mutation-tested: remove the environment fallback from ResolveConnectionString
    /// and this exits 1 with the usage message instead.</para>
    /// </summary>
    [Fact]
    public async Task Import_WithNoConnectionStringInArgv_ReadsItFromTheEnvironment()
    {
        await SeedPageWithAttachmentAsync("bytes"u8.ToArray());
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var original = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, "high");

            var (exitCode, output) = await RunCliAsync(
                "import",
                "--bundle", BundleDir,
                "--instance-id", HighInstanceId,
                "--origin-instance-id", LowInstanceId,
                "--attachments-root", HighStorageRoot);

            Assert.Equal(0, exitCode);
            Assert.Contains("Applied bundle-000001.zip", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, original);
        }

        using var high = CreateContext("high");
        Assert.Single(high.Pages.IgnoreQueryFilters().ToList());
    }

    /// <summary>The file source, for a Kubernetes or Docker secret mount. Trailing
    /// whitespace is trimmed: every editor and every k8s projection adds a newline, and a
    /// connection string with one appended fails in a way that names neither.</summary>
    [Fact]
    public async Task Import_ReadsTheConnectionStringFromAFile()
    {
        await SeedPageWithAttachmentAsync("bytes"u8.ToArray());
        Assert.Equal(0, (await RunExportAsync("--baseline", "ENG")).ExitCode);

        var secretPath = Path.Combine(_tempRoot, "connection-string");
        await File.WriteAllTextAsync(secretPath, "high\n");

        var (exitCode, output) = await RunCliAsync(
            "import",
            "--connection-string-file", secretPath,
            "--bundle", BundleDir,
            "--instance-id", HighInstanceId,
            "--origin-instance-id", LowInstanceId,
            "--attachments-root", HighStorageRoot);

        Assert.Equal(0, exitCode);
        Assert.Contains("Applied bundle-000001.zip", output);
    }

    /// <summary>No source at all is still a usage error — the new sources widen where the
    /// value may come from, never whether one is needed.</summary>
    [Fact]
    public async Task Import_WithNoConnectionStringAnywhere_PrintsUsage_Exit1()
    {
        var original = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, null);

            var (exitCode, output) = await RunCliAsync(
                "import",
                "--bundle", BundleDir,
                "--instance-id", HighInstanceId,
                "--origin-instance-id", LowInstanceId,
                "--attachments-root", HighStorageRoot);

            Assert.Equal(1, exitCode);
            Assert.Contains("RocketWiki.Sync", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, original);
        }
    }

    // --- Helpers -------------------------------------------------------------------------

    /// <summary>Seeds the low side with a space, a page and one real attachment (through
    /// the real services, so what the tamper tests attack is the bundle a running low
    /// instance would actually have produced) and returns the page's id.</summary>
    private async Task<Guid> SeedPageWithAttachmentAsync(byte[] blobBytes)
    {
        using var low = SeedLowSpace(out var actor, out var space);

        var page = await new PageService(low, LowInstanceId).CreatePageAsync(
            new CreatePageRequest(space.Id, null, "runbook", "Runbook", "# Runbook"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page.IsSuccess, $"page creation failed: {page.Error}");
        Assert.NotNull(page.Value);

        var lowStorage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
        {
            FileSystem = new FileSystemFileStorageOptions { Root = LowStorageRoot },
        }));
        var uploaded = await new AttachmentService(low, lowStorage, LowInstanceId).UploadAsync(
            new UploadAttachmentRequest(page.Value.Id, "diagram.bin", "application/octet-stream", new MemoryStream(blobBytes)),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(uploaded.IsSuccess, $"upload failed: {uploaded.Error}");

        return page.Value.Id;
    }

    /// <summary>Rewrites the events entry and re-stamps <c>manifest.PayloadSha256</c> over
    /// the result — needed by any test that is about something OTHER than the payload
    /// hash, which would otherwise fire first and prove nothing about the edit.</summary>
    private static void RewriteEventsAndRestampHash(string bundlePath, Func<string, string> rewrite)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        var entryName = BundleFormat.EventsEntryName(BundleFormat.CurrentVersion);

        var eventsEntry = archive.GetEntry(entryName)!;
        string events;
        using (var reader = new StreamReader(eventsEntry.Open()))
        {
            events = reader.ReadToEnd();
        }

        var rewritten = rewrite(events);
        eventsEntry.Delete();
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open()))
        {
            writer.Write(rewritten);
        }

        var manifestEntry = archive.GetEntry("manifest.json")!;
        string manifestJson;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestJson = reader.ReadToEnd();
        }

        var manifest = JsonNode.Parse(manifestJson)!.AsObject();
        manifest["payloadSha256"] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rewritten)));

        manifestEntry.Delete();
        // UTF8Encoding(false): StreamWriter's Encoding.UTF8 overload writes a BOM, and a
        // BOM at the head of manifest.json is a parse error, not the case under test.
        using var manifestWriter = new StreamWriter(
            archive.CreateEntry("manifest.json").Open(), new System.Text.UTF8Encoding(false));
        manifestWriter.Write(manifest.ToJsonString());
    }


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
            "--instance-id", HighInstanceId,
            "--origin-instance-id", LowInstanceId,
            "--attachments-root", HighStorageRoot);
}
