using System.Text;
using System.Text.Json;
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
/// design.md §21 + §12: markings travel with content. The controlling fact these tests
/// exist to pin is that <b>a page cannot land on the high side unmarked</b> — not via an
/// incremental upsert, not via a baseline, and not via a legacy bundle that predates the
/// feature.
/// </summary>
public class PageMarkingSyncTests : SqliteTestBase
{
    private const string LowInstanceId = "low-instance";

    protected override string DefaultLocalInstanceId => LowInstanceId;

    private static readonly AuditContext AuditCtx = new(AuditChannel.Sync, "sync-job-1", "127.0.0.1");

    private static Principal EditorPrincipal(string clearance = "TOP_SECRET", params string[] nationality) =>
        Principal.Create(
            "editor-sub",
            [],
            [
                new("clearance", new[] { clearance }),
                new("nationality", nationality.Length == 0 ? ["GB"] : nationality),
            ]);

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static Space NewExportedSpace(string key = "ENG") => new()
    {
        Key = key,
        Name = $"{key} Space",
        OriginInstanceId = LowInstanceId,
        IsExported = true,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
    };

    private static AttributeDefinition NationalityRegistry() => new()
    {
        Key = "nationality",
        ClaimName = "nationality",
        DisplayName = "Nationality",
        Type = AttributeValueType.StringArray,
        AllowedValuesJson = """["GB","US","NZ"]""",
    };

    private static (SqliteConnection Connection, RocketWikiDbContext Context) CreateSecondaryDatabase()
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

    private static IFileStorage CreateFileStorage(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "rocketwiki-marking-sync-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new FileStorageOptions { FileSystem = new FileSystemFileStorageOptions { Root = tempDir } });
        return new FileSystemFileStorage(options);
    }

    private static string CreateBundleOutputDir() =>
        Path.Combine(Path.GetTempPath(), "rocketwiki-marking-bundles-tests", Guid.NewGuid().ToString("N"));

    // --- Outbox ---------------------------------------------------------------------------

    [Fact]
    public async Task MarkingChange_OnAnExportedSpace_JournalsAPageMarkingEvent_WithTheWireNameAndCountries()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.AttributeDefinitions.Add(NationalityRegistry());
        context.SaveChanges();

        var service = new PageMarkingService(context, LowInstanceId);
        Assert.True((await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, ["US", "gb"]),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var outboxEvent = Assert.Single(context.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.PageMarking));
        using var payload = JsonDocument.Parse(outboxEvent.PayloadJson);
        Assert.Equal(page.Id, payload.RootElement.GetProperty("pageId").GetGuid());
        // The WIRE name, never the tinyint - a renumbered enum must not silently re-rank
        // a bundle already sitting on a transfer disk.
        Assert.Equal("SECRET", payload.RootElement.GetProperty("level").GetString());
        Assert.Equal(
            ["GB", "US"],
            payload.RootElement.GetProperty("eyesOnly").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task MarkingChange_JournalsTheNationalPrefix_AndItRoundTripsToTheHighSide()
    {
        // design.md §21.12: the prefix is presentational, and that is precisely why it has
        // to cross - a replica must render the same marking string as its origin, or a
        // reader comparing the two sides sees two different markings on identical content.
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.AttributeDefinitions.Add(NationalityRegistry());
        lowContext.SaveChanges();

        var created = await new PageService(lowContext, LowInstanceId).CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        Assert.True((await new PageMarkingService(lowContext, LowInstanceId).SetAsync(
            new SetPageMarkingRequest(created.Value.Id, ClassificationLevel.Secret, ["GB"], "nato"),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var outboxEvent = Assert.Single(lowContext.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.PageMarking));
        using (var payload = JsonDocument.Parse(outboxEvent.PayloadJson))
        {
            Assert.Equal("NATO", payload.RootElement.GetProperty("prefix").GetString());
        }

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage)
                .ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Countries)
                    .Single(m => m.PageId == created.Value.Id);
                Assert.Equal("NATO", marking.Prefix);
                // The whole point: the replica renders the identical string.
                Assert.Equal("NATO SECRET [GB EYES ONLY]", marking.ToMarking().Format());
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_AnUpsertCarryingNoPrefixKey_LandsWithNoPrefix_NeverAnInventedUk()
    {
        // A bundle from before prefixes existed said nothing about a national qualifier,
        // and defaulting one in would assert something its origin never said. Only the
        // LEVEL gets a fail-closed substitution, because only the level gates anything.
        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new
        {
            pageId,
            spaceId,
            parentPageId = (Guid?)null,
            ancestorPath = "/",
            slug = "legacy",
            title = "Legacy",
            sortOrder = 0,
            content = "# Legacy",
            revisionNumber = 1,
            // A marking, but from the pre-prefix era: no "prefix" key at all.
            marking = new { level = "SECRET", eyesOnly = Array.Empty<string>() },
        });

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundlePath = WriteLegacyBundle(outputDir, spaceId, payload);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                highContext.Spaces.Add(new Space
                {
                    Id = spaceId,
                    Key = "LEG",
                    Name = "Legacy",
                    OriginInstanceId = LowInstanceId,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserId = Guid.NewGuid(),
                });
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Single(m => m.PageId == pageId);
                Assert.Equal(ClassificationLevel.Secret, marking.Level);
                Assert.Null(marking.Prefix);
                Assert.Equal("SECRET", marking.ToMarking().Format());
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task MarkingChange_OnANonExportedSpace_JournalsNothing()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        space.IsExported = false;
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageMarkingService(context, LowInstanceId);
        Assert.True((await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, []),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        Assert.Empty(context.SyncOutboxEvents);
    }

    // --- Round trips ------------------------------------------------------------------------

    [Fact]
    public async Task Incremental_MarkingChange_RoundTripsToTheHighSide()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.AttributeDefinitions.Add(NationalityRegistry());
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var markingService = new PageMarkingService(lowContext, LowInstanceId);
        Assert.True((await markingService.SetAsync(
            new SetPageMarkingRequest(created.Value.Id, ClassificationLevel.Secret, ["GB", "US"]),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage)
                .ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var result = await new BundleImportService(highContext, storage)
                    .ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Countries)
                    .Single(m => m.PageId == created.Value.Id);
                Assert.Equal(ClassificationLevel.Secret, marking.Level);
                Assert.Equal(
                    ["GB", "US"],
                    marking.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal));
                // Applied by sync, so no local actor (§21, same shape as a page property).
                Assert.Null(marking.SetByUserId);

                // And the high side really enforces it: an OFFICIAL-cleared reader with a
                // space grant still cannot see the page.
                highContext.AccessRules.Add(EditorGrant(space.Id));
                highContext.SaveChanges();
                var readService = new PageReadService(highContext);
                Assert.IsType<ReadResult<Page>.Denied>(await readService.GetPageAsync(
                    created.Value.Id, Principal.Create("high-user", [])));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Baseline_CarriesEveryPagesMarking_SoNothingLandsUnmarked()
    {
        // The gap that would matter most: a space baselined after markings were applied
        // must not deliver its whole back catalogue as unmarked content.
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space, "classified");

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.TopSecret, "GB"));
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage)
                .ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == page.Id);
                Assert.Equal(ClassificationLevel.TopSecret, marking.Level);
                Assert.Equal(["GB"], marking.Countries.Select(c => c.CountryValue));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_AnUpsertCarryingNoMarking_LandsTheNewPageAtTopSecret_NotOfficial()
    {
        // A legacy (pre-§21) bundle. Content arriving from a lower instance without a
        // declared classification is exactly where guessing OFFICIAL would be a
        // cross-boundary disclosure, so it arrives visible to almost nobody and a
        // high-side admin marks it down after review.
        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var legacyPayload = JsonSerializer.Serialize(new
        {
            pageId,
            spaceId,
            parentPageId = (Guid?)null,
            ancestorPath = "/",
            slug = "legacy",
            title = "Legacy",
            sortOrder = 0,
            content = "# Legacy",
            revisionNumber = 1,
        });

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundlePath = WriteLegacyBundle(outputDir, spaceId, legacyPayload);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                highContext.Spaces.Add(new Space
                {
                    Id = spaceId,
                    Key = "LEG",
                    Name = "Legacy",
                    OriginInstanceId = LowInstanceId,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserId = Guid.NewGuid(),
                });
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                Assert.Equal(
                    ClassificationLevel.TopSecret,
                    highContext.PageMarkings.Single(m => m.PageId == pageId).Level);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_ALegacyUpsertForAnAlreadyMarkedPage_LeavesTheMarkingAlone()
    {
        // The other half of the rule: silence in a payload must never re-classify a page
        // the high side already holds a marking for, in either direction.
        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var legacyPayload = JsonSerializer.Serialize(new
        {
            pageId,
            spaceId,
            parentPageId = (Guid?)null,
            ancestorPath = "/",
            slug = "legacy",
            title = "Legacy (updated)",
            sortOrder = 0,
            content = "# Legacy updated",
            revisionNumber = 2,
        });

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundlePath = WriteLegacyBundle(outputDir, spaceId, legacyPayload);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var space = new Space
                {
                    Id = spaceId,
                    Key = "LEG",
                    Name = "Legacy",
                    OriginInstanceId = LowInstanceId,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserId = Guid.NewGuid(),
                };
                var existing = TestData.NewPage(space, "legacy");
                existing.Id = pageId;
                highContext.Spaces.Add(space);
                highContext.Pages.Add(existing);
                highContext.PageMarkings.Add(TestData.NewMarking(existing, ClassificationLevel.Secret, "GB"));
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == pageId);
                Assert.Equal(ClassificationLevel.Secret, marking.Level);
                Assert.Equal(["GB"], marking.Countries.Select(c => c.CountryValue));
                Assert.Equal("Legacy (updated)", highContext.Pages.Single(p => p.Id == pageId).Title);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>A hand-built format-1 bundle: one PageUpsert line with no <c>marking</c>
    /// key, exactly what a pre-§21 low side produced.</summary>
    private static string WriteLegacyBundle(string outputDirectory, Guid spaceId, string payloadJson)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var record = new NdjsonEventRecord("LEG", spaceId, 1, nameof(SyncEventType.PageUpsert), payloadJson, DateTime.UtcNow);
        var ndjson = JsonSerializer.Serialize(record, jsonOptions) + Environment.NewLine;
        var ndjsonBytes = Encoding.UTF8.GetBytes(ndjson);

        var manifest = new BundleManifest(
            LowInstanceId, 1, null,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ndjsonBytes)),
            new Dictionary<string, SpaceEventRange> { ["LEG"] = new(spaceId, 1, 1, 1) },
            BundleFormat.LegacyVersion);

        var bundlePath = Path.Combine(outputDirectory, "bundle-000001.zip");
        using (var fileStream = new FileStream(bundlePath, FileMode.CreateNew))
        using (var archive = new System.IO.Compression.ZipArchive(fileStream, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions));
            WriteEntry(archive, "events.ndjson", ndjsonBytes);
        }

        return bundlePath;
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName);
        using var entryStream = entry.Open();
        entryStream.Write(bytes);
    }
}
