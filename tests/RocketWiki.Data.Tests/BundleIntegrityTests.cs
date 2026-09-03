using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Sync;
using RocketWiki.Data.Services;
using RocketWiki.Storage;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §12, the two properties of a bundle that are about the TRANSFER MEDIUM
/// rather than about ordering: that what arrives is what was sent, and that reading it
/// cannot itself take the importing host down.
///
/// <para><b>Attachment bytes.</b> The manifest hash chain covers the events file and
/// nothing else — the attachment binaries live in their own <c>blobs/</c> entries, outside
/// <c>PayloadSha256</c> and outside the chain. They were streamed straight into storage
/// and <c>Attachment.ContentHash</c> was set from the hash the bundle DECLARED, never from
/// the bytes, so a file could be substituted anywhere on the medium with nothing detecting
/// it — on the one boundary whose whole purpose is that only vetted content crosses. The
/// tests below are the courier: build a bundle, change what is inside it, watch the import
/// refuse.</para>
///
/// <para><b>Decompression.</b> A bundle is a zip, and every integrity check the format has
/// happens after something has been decompressed — so none of them stops a few hundred
/// kilobytes of compressed zeroes from expanding into the importer's memory. The ceilings
/// (<see cref="BundleLimits"/>) are injected small here so the refusal can be proven with
/// kilobytes; production uses <see cref="BundleLimits.Default"/>, whose values are asserted
/// at the bottom so shrinking one to make a test pass is a visible edit.</para>
/// </summary>
public class BundleIntegrityTests : SqliteTestBase
{
    private const string LowInstanceId = "low-instance";

    protected override string DefaultLocalInstanceId => LowInstanceId;

    private static readonly AuditContext AuditCtx = new(AuditChannel.Sync, "sync-job-1", "127.0.0.1");

    private static Principal EditorPrincipal() => Principal.Create("editor-sub", []);

    // --- Attachment blob integrity (the courier tests) ---------------------------------

    /// <summary>
    /// The finding, reproduced and closed: a bundle's attachment bytes are replaced after
    /// export, the entry keeps the name (and therefore the declared content hash) it had,
    /// and the import refuses.
    ///
    /// <para>Note what makes the check total rather than cosmetic: to substitute content
    /// AND keep the entry name matching, an attacker would need a SHA-256 preimage; to
    /// change the name instead, they would have to change the event line that references
    /// it, and that line is inside the bytes <c>PayloadSha256</c> covers. So re-hashing the
    /// blob against its own entry name extends the existing chain over attachment content
    /// with no change to the bundle format.</para>
    ///
    /// <para>Mutation-tested: with <c>VerifyBlobIntegrityAsync</c> removed from
    /// ImportCoreAsync, this test fails — the import succeeds and the tampered bytes land
    /// in high-side storage under a row asserting a hash they do not have.</para>
    /// </summary>
    [Fact]
    public async Task Import_AttachmentBlobBytesReplaced_Refused_AndNothingLands()
    {
        await using var fixture = await BundleWithAttachmentAsync("the original diagram bytes"u8.ToArray());

        // Swap the bytes, keep the entry name. Nothing else in the bundle changes: the
        // manifest, its chain hash and the events payload hash all still verify, so ONLY
        // a blob-level check can catch this.
        ReplaceBlobBytes(fixture.BundlePath, "tampered - a different file entirely"u8.ToArray());

        var result = await fixture.ImportAsync();

        var error = Assert.IsType<BundleBlobTamperedError>(result.Error);
        Assert.Contains("hash", error.Reason, StringComparison.OrdinalIgnoreCase);

        // Refused BEFORE anything was applied: no rows, and — the reason the check is a
        // pre-pass rather than an in-loop guard — no orphaned bytes in high-side storage
        // either. A partial write here would be permanent: the rows evaporate with the
        // unsaved change set, the blob does not, and a corrected re-import mints a fresh
        // storage key.
        Assert.Empty(fixture.HighContext.Attachments.IgnoreQueryFilters().ToList());
        Assert.Empty(fixture.HighContext.Pages.IgnoreQueryFilters().ToList());
        Assert.Empty(fixture.HighContext.SyncImportStates.ToList());
        Assert.Empty(StoredBlobFiles(fixture.HighStorageDir));
    }

    /// <summary>The control the tamper test needs to mean anything: the SAME bundle,
    /// untouched, imports and puts the exact bytes on the other side.</summary>
    [Fact]
    public async Task Import_UntamperedAttachmentBlob_Lands_WithByteIdenticalContent()
    {
        var originalBytes = "the original diagram bytes"u8.ToArray();
        await using var fixture = await BundleWithAttachmentAsync(originalBytes);

        var result = await fixture.ImportAsync();

        Assert.True(result.IsSuccess, $"import failed: {result.Error}");
        var landed = Assert.Single(fixture.HighContext.Attachments.IgnoreQueryFilters().ToList());
        Assert.Equal(SHA256.HashData(originalBytes), landed.ContentHash);

        await using var readBack = await fixture.HighStorage.OpenReadAsync(landed.StorageKey, CancellationToken.None);
        using var buffer = new MemoryStream();
        await readBack.CopyToAsync(buffer);
        Assert.Equal(originalBytes, buffer.ToArray());
    }

    /// <summary>
    /// The naming invariant the whole scheme rests on, checked rather than assumed: a
    /// <c>blobs/</c> entry whose name is not a SHA-256 hex digest is refused. Without
    /// this, "the entry name is the content hash" would be a convention the importer
    /// merely hoped for — and an entry named anything at all could sit in a bundle
    /// unverified, waiting for a later reference.
    /// </summary>
    [Fact]
    public async Task Import_BlobEntryNotNamedForItsHash_Refused()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());

        using (var archive = ZipFile.Open(fixture.BundlePath, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("blobs/not-a-content-hash");
            await using var stream = entry.Open();
            await stream.WriteAsync("smuggled"u8.ToArray());
        }

        var result = await fixture.ImportAsync();

        var error = Assert.IsType<BundleBlobTamperedError>(result.Error);
        Assert.Contains("not-a-content-hash", error.Reason, StringComparison.Ordinal);
    }

    // --- Decompression bounds ----------------------------------------------------------

    /// <summary>
    /// A blob larger than its ceiling. This is the case that proves the bound is real
    /// rather than declarative: blobs are hashed by streaming, with no up-front
    /// <c>entry.Length</c> check at all, so the only thing that can stop this is the
    /// wrapper counting bytes as they decompress.
    ///
    /// <para>Mutation-tested: with <c>BoundedReadStream.Count</c>'s throw removed, the
    /// oversized blob hashes happily and the import proceeds.</para>
    /// </summary>
    [Fact]
    public async Task Import_BlobLargerThanItsCeiling_RefusedAsTooLarge()
    {
        await using var fixture = await BundleWithAttachmentAsync(new byte[4096]);

        var result = await fixture.ImportAsync(new BundleLimits { MaxBlobBytes = 64 });

        var error = Assert.IsType<BundleTooLargeError>(result.Error);
        Assert.Contains("blobs/", error.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.HighContext.Pages.IgnoreQueryFilters().ToList());
    }

    /// <summary>The events file, which is read whole into memory and then decoded and
    /// split — the entry whose size actually costs the importer.</summary>
    [Fact]
    public async Task Import_EventsFileLargerThanItsCeiling_RefusedAsTooLarge()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());

        var result = await fixture.ImportAsync(new BundleLimits { MaxEventsBytes = 32 });

        var error = Assert.IsType<BundleTooLargeError>(result.Error);
        Assert.Contains("events", error.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entry count, refused from the central directory alone — before a single
    /// byte is decompressed, which is the point of checking it first.</summary>
    [Fact]
    public async Task Import_TooManyEntries_RefusedAsTooLarge()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());

        var result = await fixture.ImportAsync(new BundleLimits { MaxEntryCount = 2 });

        var error = Assert.IsType<BundleTooLargeError>(result.Error);
        Assert.Contains("entries", error.Reason, StringComparison.Ordinal);
    }

    /// <summary>The sum across entries. The per-entry ceilings alone would still admit
    /// MaxEntryCount × MaxBlobBytes, which is the same attack with more entries.</summary>
    [Fact]
    public async Task Import_TotalDeclaredExpansionOverCeiling_RefusedAsTooLarge()
    {
        await using var fixture = await BundleWithAttachmentAsync(new byte[4096]);

        var result = await fixture.ImportAsync(new BundleLimits { MaxTotalUncompressedBytes = 512 });

        var error = Assert.IsType<BundleTooLargeError>(result.Error);
        Assert.Contains("uncompressed", error.Reason, StringComparison.Ordinal);
    }

    /// <summary>Line count, which the byte ceiling only bounds loosely: each line becomes
    /// an object held for the whole import, so the object count gets its own limit rather
    /// than one inferred from the shortest legal line.</summary>
    [Fact]
    public async Task Import_TooManyEventLines_RefusedAsTooLarge()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray(), extraPages: 4);

        var result = await fixture.ImportAsync(new BundleLimits { MaxEventLines = 2 });

        var error = Assert.IsType<BundleTooLargeError>(result.Error);
        Assert.Contains("lines", error.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ceilings production actually runs with, asserted here so that lowering one to
    /// make something pass is an edit to a test that says what it is protecting rather
    /// than a silent change of posture. They are generous on purpose: the job is to bound
    /// the pathological case, not to second-guess a legitimate export.
    /// </summary>
    [Fact]
    public void DefaultLimits_AreTheDocumentedCeilings()
    {
        var limits = BundleLimits.Default;

        Assert.Equal(100_000, limits.MaxEntryCount);
        Assert.Equal(8L * 1024 * 1024, limits.MaxManifestBytes);
        Assert.Equal(256L * 1024 * 1024, limits.MaxEventsBytes);
        Assert.Equal(1L * 1024 * 1024 * 1024, limits.MaxBlobBytes);
        Assert.Equal(16L * 1024 * 1024 * 1024, limits.MaxTotalUncompressedBytes);
        Assert.Equal(5_000_000, limits.MaxEventLines);
    }

    // --- Malformed content is a refusal, not a crash (design.md §7) ---------------------

    /// <summary>
    /// An NDJSON line that is literally <c>null</c>. It used to reach
    /// <c>Deserialize(...)!</c> and become a NullReferenceException three frames from the
    /// bad line; the CLI's refusal filter never saw it, so a corrupt bundle produced a
    /// stack trace instead of the <c>sync.import.refused</c> row §7 relies on. Now it is a
    /// named refusal naming the line, of a type that filter catches.
    /// </summary>
    [Fact]
    public async Task Import_NullEventLine_ThrowsANamedRefusal_NotANullReference()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());
        RewriteEventsEntry(fixture.BundlePath, existing => existing + "null\n");

        // The payload hash no longer matches, so re-stamp it: this test is about the line,
        // not about the hash check that would otherwise fire first.
        RestampPayloadHash(fixture.BundlePath);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync());
        Assert.Contains("not an event record", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>An event type name this build does not know. <c>Enum.Parse</c> threw a bare
    /// ArgumentException — a refusal in substance that escaped the CLI's filter as a crash.
    /// Refusing rather than skipping is deliberate: absorbing an unknown is the one thing
    /// §12 never does.</summary>
    [Fact]
    public async Task Import_UnknownEventTypeName_ThrowsANamedRefusal()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());
        RewriteEventsEntry(fixture.BundlePath, existing => existing.Replace("\"PageUpsert\"", "\"Teleport\"", StringComparison.Ordinal));
        RestampPayloadHash(fixture.BundlePath);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync());
        Assert.Contains("Teleport", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A <c>contentHash</c> that is not a SHA-256 hex digest. It used to reach
    /// <c>Convert.FromHexString</c> as a FormatException (a crash), or — worse, if it
    /// happened to be valid hex of the wrong length — reach a <c>binary(32)</c> column as a
    /// truncation error, after the blob was already written to storage.</summary>
    [Fact]
    public async Task Import_AttachmentContentHashNotAHexDigest_ThrowsANamedRefusal()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());
        RewriteEventsEntry(fixture.BundlePath, existing =>
        {
            var hashHex = Convert.ToHexString(SHA256.HashData("bytes"u8.ToArray()));
            // Escaped, because the hash lives inside a JSON string inside a JSON string.
            return existing.Replace(hashHex, new string('Z', 64), StringComparison.Ordinal);
        });
        RestampPayloadHash(fixture.BundlePath);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync());
        Assert.Contains("SHA-256", thrown.Message, StringComparison.Ordinal);
    }

    // --- The bundle declares its own origin (design.md §12) ------------------------------

    /// <summary>
    /// <c>manifest.InstanceId</c> was written by every exporter and read by nothing. The
    /// importer keyed every replica space, the <c>SyncImportState</c> position and every
    /// per-space sequence off the <c>originInstanceId</c> ARGUMENT instead, so a bundle
    /// from instance A imported as if it came from B spliced two streams into one position
    /// — and the strict ordering that position exists to enforce became an ordering over
    /// nothing. One wrong flag on a scheduled job is all it takes.
    ///
    /// <para>Mutation-tested: remove the comparison and the import succeeds, landing A's
    /// content under B's stream position.</para>
    /// </summary>
    [Fact]
    public async Task Import_BundleDeclaringADifferentOrigin_IsRefused()
    {
        await using var fixture = await BundleWithAttachmentAsync("bytes"u8.ToArray());

        var result = await new BundleImportService(fixture.HighContext, fixture.HighStorage, "high-instance")
            .ImportAsync(fixture.BundlePath, "some-other-low-instance", AuditCtx);

        var error = Assert.IsType<BundleOriginMismatchError>(result.Error);
        Assert.Equal(LowInstanceId, error.DeclaredInstanceId);
        Assert.Equal("some-other-low-instance", error.ExpectedInstanceId);
        Assert.Empty(fixture.HighContext.SyncImportStates.ToList());
        Assert.Empty(fixture.HighContext.Pages.IgnoreQueryFilters().ToList());
    }

    // --- Export-side ownership (design.md §12) -------------------------------------------

    /// <summary>
    /// "Only a native space can be exported; a replica must never emit sync events for
    /// content it doesn't own" — enforced in the SERVICE, not only in the console tool that
    /// happens to be its sole caller today.
    ///
    /// <para>The incremental path never had this gap: SyncOutboxWriter owns the gate, so
    /// nothing can journal an event for a space that should not emit one. The baseline path
    /// put the rule in RocketWiki.Sync's argument handling, leaving a public method that
    /// would produce a full snapshot — content, restrictions, attachment bytes — of a
    /// REPLICA the moment a second caller appeared.</para>
    ///
    /// <para>Mutation-tested: remove either check from ExportBaselineCoreAsync and the
    /// matching case below stops throwing and writes a bundle.</para>
    /// </summary>
    [Theory]
    [InlineData(false, true, "not flagged exported")]  // exported flag off
    [InlineData(true, false, "must never emit sync content it does not own")] // replica
    public async Task ExportBaseline_OnASpaceThatMustNotEmit_IsRefusedByTheService(
        bool isExported, bool isNative, string expectedFragment)
    {
        var actor = TestData.NewUser();
        var space = new Space
        {
            Key = "ENG",
            Name = "ENG Space",
            OriginInstanceId = isNative ? LowInstanceId : "somebody-else",
            IsExported = isExported,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
        };

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.SaveChanges();

        var storage = CreateStorage(out var storageDir);
        var outputDir = Path.Combine(Path.GetTempPath(), "rocketwiki-bundle-integrity", Guid.NewGuid().ToString("N"));
        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new BundleExportService(lowContext, storage).ExportBaselineAsync(space.Id, outputDir, LowInstanceId));

            Assert.Contains(expectedFragment, thrown.Message, StringComparison.Ordinal);

            // And nothing was written: a refusal that still leaves a bundle on the transfer
            // directory is not a refusal.
            Assert.True(!Directory.Exists(outputDir) || Directory.GetFiles(outputDir).Length == 0);
        }
        finally
        {
            foreach (var dir in new[] { storageDir, outputDir })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    // --- Fixture ------------------------------------------------------------------------

    /// <summary>
    /// A real exported bundle carrying one page and one attachment, plus the second
    /// (high-side) database and storage root to import it into. Built through the real
    /// services rather than hand-written rows, so what the tamper tests attack is the
    /// bundle a running low instance would actually have produced.
    /// </summary>
    private sealed class BundleFixture : IAsyncDisposable
    {
        public required string BundlePath { get; init; }
        public required RocketWikiDbContext HighContext { get; init; }
        public required IFileStorage HighStorage { get; init; }
        public required string HighStorageDir { get; init; }
        public required SqliteConnection HighConnection { get; init; }
        public required string LowStorageDir { get; init; }
        public required string OutputDir { get; init; }

        public Task<PageMutationResult<ImportedBundleSummary>> ImportAsync(BundleLimits? limits = null) =>
            new BundleImportService(HighContext, HighStorage, "high-instance", limits)
                .ImportAsync(BundlePath, LowInstanceId, AuditCtx);

        public ValueTask DisposeAsync()
        {
            HighContext.Dispose();
            HighConnection.Dispose();
            foreach (var dir in new[] { HighStorageDir, LowStorageDir, OutputDir })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private async Task<BundleFixture> BundleWithAttachmentAsync(byte[] blobBytes, int extraPages = 0)
    {
        var actor = TestData.NewUser();
        var space = new Space
        {
            Key = "ENG",
            Name = "ENG Space",
            OriginInstanceId = LowInstanceId,
            IsExported = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
        };

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        var editorGrant = new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        };
        lowContext.AccessRules.AddRange(TestData.AccessGrantMirroring(editorGrant), editorGrant);
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "runbook", "Runbook", "# Runbook"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess, $"page creation failed: {created.Error}");
        var page = created.Value;
        Assert.NotNull(page);

        for (var i = 0; i < extraPages; i++)
        {
            Assert.True((await pageService.CreatePageAsync(
                new CreatePageRequest(space.Id, null, $"extra-{i}", $"Extra {i}", "# Extra"),
                EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);
        }

        var lowStorage = CreateStorage(out var lowStorageDir);
        var highStorage = CreateStorage(out var highStorageDir);
        var outputDir = Path.Combine(Path.GetTempPath(), "rocketwiki-bundle-integrity", Guid.NewGuid().ToString("N"));

        Assert.True((await new AttachmentService(lowContext, lowStorage, LowInstanceId).UploadAsync(
            new UploadAttachmentRequest(page.Id, "diagram.bin", "application/octet-stream", new MemoryStream(blobBytes)),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var bundle = await new BundleExportService(lowContext, lowStorage)
            .ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

        var (highConnection, highContext) = CreateHighDatabase();
        return new BundleFixture
        {
            BundlePath = bundle.BundleFilePath,
            HighContext = highContext,
            HighConnection = highConnection,
            HighStorage = highStorage,
            HighStorageDir = highStorageDir,
            LowStorageDir = lowStorageDir,
            OutputDir = outputDir,
        };
    }

    private static (SqliteConnection Connection, RocketWikiDbContext Context) CreateHighDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<RocketWikiDbContext>().UseSqlite(connection).Options;
        var context = new RocketWikiDbContext(options);
        context.Database.EnsureCreated();
        return (connection, context);
    }

    private static IFileStorage CreateStorage(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "rocketwiki-bundle-integrity", Guid.NewGuid().ToString("N"));
        return new FileSystemFileStorage(Options.Create(
            new FileStorageOptions { FileSystem = new FileSystemFileStorageOptions { Root = tempDir } }));
    }

    private static string[] StoredBlobFiles(string storageDir) =>
        Directory.Exists(storageDir) ? Directory.GetFiles(storageDir, "*", SearchOption.AllDirectories) : [];

    /// <summary>The tamper itself: the single <c>blobs/</c> entry's content is replaced and
    /// its NAME left alone, so the bundle still declares the original content hash for it.</summary>
    private static void ReplaceBlobBytes(string bundlePath, byte[] replacement)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        var blob = Assert.Single(archive.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.Ordinal)).ToList());
        var name = blob.FullName;
        blob.Delete();

        using var stream = archive.CreateEntry(name).Open();
        stream.Write(replacement);
    }

    private static void RewriteEventsEntry(string bundlePath, Func<string, string> rewrite)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        var entryName = BundleFormat.EventsEntryName(BundleFormat.CurrentVersion);
        var entry = archive.GetEntry(entryName)!;

        string text;
        using (var reader = new StreamReader(entry.Open()))
        {
            text = reader.ReadToEnd();
        }

        entry.Delete();
        using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
        writer.Write(rewrite(text));
    }

    /// <summary>Recomputes <c>manifest.PayloadSha256</c> over the (rewritten) events entry.
    /// Only for tests that are about something OTHER than the payload hash — without it
    /// that check fires first and the test would prove nothing about the line it edited.</summary>
    private static void RestampPayloadHash(string bundlePath)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        var eventsEntry = archive.GetEntry(BundleFormat.EventsEntryName(BundleFormat.CurrentVersion))!;

        byte[] eventsBytes;
        using (var stream = eventsEntry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            eventsBytes = memory.ToArray();
        }

        var manifestEntry = archive.GetEntry("manifest.json")!;
        string manifestJson;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestJson = reader.ReadToEnd();
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(manifestJson)!.AsObject();
        node["payloadSha256"] = Convert.ToHexString(SHA256.HashData(eventsBytes));

        manifestEntry.Delete();
        // UTF8Encoding(false): StreamWriter's Encoding.UTF8 overload writes a BOM, and a
        // BOM at the head of manifest.json is a JsonException, not a payload-hash test.
        using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
        writer.Write(node.ToJsonString());
    }
}
