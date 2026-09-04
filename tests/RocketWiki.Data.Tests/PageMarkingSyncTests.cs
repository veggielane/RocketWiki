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
using RocketWiki.Core.Tests.Access;
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

    private static Principal EditorPrincipal(params string[] nationality) =>
        Principal.Create(
            "editor-sub",
            [],
            [new("nationality", nationality.Length == 0 ? ["UK"] : nationality)]);

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.RoleGrant,
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
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageMarkingService(context, LowInstanceId);
        Assert.True((await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, ["US", "uk"], [], UkPrefix: true),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var outboxEvent = Assert.Single(context.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.PageMarking));
        using var payload = JsonDocument.Parse(outboxEvent.PayloadJson);
        Assert.Equal(page.Id, payload.RootElement.GetProperty("pageId").GetGuid());
        // The WIRE name, never the tinyint - a renumbered enum must not silently re-rank
        // a bundle already sitting on a transfer disk.
        Assert.Equal("SECRET", payload.RootElement.GetProperty("level").GetString());
        Assert.Equal(
            ["UK", "US"],
            payload.RootElement.GetProperty("eyesOnly").EnumerateArray().Select(e => e.GetString()));
        // design.md §21.10: the selectors object is ALWAYS present - {} here - so a missing
        // key on the import side is unambiguously a pre-selector bundle.
        Assert.Equal(JsonValueKind.Object, payload.RootElement.GetProperty("selectors").ValueKind);
        Assert.Empty(payload.RootElement.GetProperty("selectors").EnumerateObject());
    }

    [Fact]
    public async Task MarkingChange_JournalsThePrefixToggle_AndItRoundTripsToTheHighSide()
    {
        // design.md §21.12: the prefix is presentational, and that is precisely why it has
        // to cross - a replica must render the same marking string as its origin, or a
        // reader comparing the two sides sees two different markings on identical content.
        // Toggled OFF here, so the payload carries an explicit null and the high side
        // renders the bare level rather than inventing the UK default.
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        lowContext.SaveChanges();

        var created = await new PageService(lowContext, LowInstanceId).CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        Assert.True((await new PageMarkingService(lowContext, LowInstanceId).SetAsync(
            new SetPageMarkingRequest(created.Value.Id, ClassificationLevel.Secret, ["UK"], [], UkPrefix: false),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var outboxEvent = Assert.Single(lowContext.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.PageMarking));
        using (var payload = JsonDocument.Parse(outboxEvent.PayloadJson))
        {
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("prefix").ValueKind);
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
                Assert.Null(marking.Prefix);
                // The whole point: the replica renders the identical string.
                Assert.Equal("SECRET UK EYES ONLY", marking.ToMarking().Format(SelectorCatalog.Empty));
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
                Assert.Equal("SECRET", marking.ToMarking().Format(SelectorCatalog.Empty));
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
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageMarkingService(context, LowInstanceId);
        Assert.True((await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, [], [], UkPrefix: true),
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
        lowContext.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var markingService = new PageMarkingService(lowContext, LowInstanceId);
        Assert.True((await markingService.SetAsync(
            new SetPageMarkingRequest(created.Value.Id, ClassificationLevel.Secret, ["UK", "US"], [], UkPrefix: true),
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
                    ["UK", "US"],
                    marking.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal));
                // Applied by sync, so no local actor (§21, same shape as a page property).
                Assert.Null(marking.SetByUserId);

                // And the high side really enforces it: an OFFICIAL-cleared reader with a
                // space grant still cannot see the page.
                highContext.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
        lowContext.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.TopSecret, "UK"));
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
                Assert.Equal(["UK"], marking.Countries.Select(c => c.CountryValue));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_APayloadWithNoMarking_LandsUnreadableByEveryone_NotAsAPlainTopSecret()
    {
        // A legacy (pre-§21) bundle. Content arriving from a lower instance without a
        // declared classification is exactly where guessing OFFICIAL would be a
        // cross-boundary disclosure, so it lands UNKNOWN (PageMarking.IsUnavailable) and
        // readable by nobody until the origin declares a marking. It used to land as a
        // bare TOP SECRET row, which denied all but the highest-cleared while the level
        // gated; now that the level is presentational (§21.12) such a row would gate
        // nobody - the fail-open this test exists to keep closed. So the assertion is
        // through the REAL gate, for the most generous caller there is: a Space-admin
        // whose access grant confers every selector value, holding every nationality.
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
                highContext.Spaces.Add(HighSideSpace(spaceId));
                highContext.AccessRules.AddRange(
                    AccessGrantWith(spaceId, TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South),
                    SpaceAdminGrant(spaceId));
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                var row = highContext.PageMarkings.Include(m => m.Countries).Include(m => m.Selectors).Single(m => m.PageId == pageId);
                Assert.True(row.IsUnavailable);
                Assert.True(row.ToMarking().IsUnavailable);
                // Still renders as the sentinel's bare TOP SECRET - the visual signal that
                // something is missing - but that is display, not what denies.
                Assert.Equal("TOP SECRET", row.ToMarking().Format(TestCatalogs.Fruit));

                var denied = Assert.IsType<ReadResult<Page>.Denied>(
                    await new PageReadService(highContext).GetPageAsync(pageId, EveryoneAdmittingPrincipal()));
                Assert.Equal("marking:unavailable", denied.Reason);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_ADeclaredMarking_ClearsTheUnavailableFlag_AndThePageBecomesReadable()
    {
        // The recovery path: the flag means "unknown", and the origin stating the marking
        // makes it known. A second bundle carrying a declared marking for the same page
        // overwrites the unavailable row (the "never re-classify from silence" rule only
        // protects a row when the payload is silent), and the page opens for whoever the
        // declared marking admits.
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
            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                highContext.Spaces.Add(HighSideSpace(spaceId));
                highContext.AccessRules.Add(TestData.AccessGrantMirroring(EditorGrant(spaceId)));
                highContext.SaveChanges();

                var unknownBundle = WriteLegacyBundle(Path.Combine(outputDir, "unknown"), spaceId, legacyPayload);
                Assert.True((await new BundleImportService(highContext, storage).ImportAsync(unknownBundle, LowInstanceId, AuditCtx)).IsSuccess);
                Assert.True(highContext.PageMarkings.Single(m => m.PageId == pageId).IsUnavailable);
                Assert.IsType<ReadResult<Page>.Denied>(await new PageReadService(highContext).GetPageAsync(pageId, Principal.Create("reader", [])));

                // Chained as bundle 2: an unchained repeat of bundle 1 is skipped as a
                // duplicate (success, nothing applied), which would make this test pass for
                // the wrong reason.
                var previousHash = highContext.SyncImportStates.Single(s => s.OriginInstanceId == LowInstanceId).LastManifestHash;
                var declaredBundle = WriteLegacyBundle(Path.Combine(outputDir, "declared"), spaceId, UpsertPayload(pageId, spaceId, new
                {
                    level = "OFFICIAL", eyesOnly = Array.Empty<string>(), prefix = "UK", selectors = new Dictionary<string, string>(),
                }), bundleNumber: 2, previousManifestHash: previousHash, sequence: 2);
                var second = await new BundleImportService(highContext, storage).ImportAsync(declaredBundle, LowInstanceId, AuditCtx);
                Assert.True(second.IsSuccess, $"{second.Error}");
                Assert.Equal(1, second.Value.EventsApplied);

                var row = highContext.PageMarkings.Include(m => m.Countries).Include(m => m.Selectors).Single(m => m.PageId == pageId);
                Assert.False(row.IsUnavailable);
                Assert.Equal(ClassificationLevel.Official, row.Level);
                Assert.IsType<ReadResult<Page>.Found>(await new PageReadService(highContext).GetPageAsync(pageId, Principal.Create("reader", [])));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_AnUnavailableMarking_CrossesAsNoMarking_NeverAsABareTopSecret()
    {
        // The other side of the same trap: a row that says "unknown" must not be
        // serialized as the sentinel's parts, or the next instance stores a bare TOP
        // SECRET that gates nobody. It crosses as a null marking, which the importer
        // reads as unknown - so "unknown" survives every hop.
        var space = NewExportedSpace();
        var page = TestData.NewPage(space, "unknown");

        using var lowContext = CreateContext();
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        var lowRow = TestData.NewMarking(page, ClassificationLevel.Official);
        lowRow.IsUnavailable = true;
        lowContext.PageMarkings.Add(lowRow);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage).ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            using (var archive = System.IO.Compression.ZipFile.OpenRead(bundleInfo.BundleFilePath))
            using (var reader = new StreamReader(archive.GetEntry(BundleFormat.EventsEntryName(BundleFormat.CurrentVersion))!.Open()))
            {
                // One line per event; the page's payload rides inside it as a JSON string.
                var line = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
                using var record = JsonDocument.Parse(line);
                using var payload = JsonDocument.Parse(record.RootElement.GetProperty("payloadJson").GetString()!);
                Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("marking").ValueKind);
                Assert.DoesNotContain("TOP_SECRET", line, StringComparison.Ordinal);
            }

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                Assert.True(highContext.PageMarkings.Single(m => m.PageId == page.Id).IsUnavailable);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>The most generous principal the model can describe: in every group a
    /// fixture uses, holding every nationality in the fixed vocabulary.</summary>
    private static Principal EveryoneAdmittingPrincipal() =>
        Principal.Create("high-admin", ["engineering", "editors", "readers"], [new("nationality", NationalCaveatVocabulary.Values.ToArray())]);

    private static AccessRule SpaceAdminGrant(Guid spaceId)
    {
        var grant = EditorGrant(spaceId);
        grant.Role = SpaceRole.SpaceAdmin;
        return grant;
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
                highContext.PageMarkings.Add(TestData.NewMarking(existing, ClassificationLevel.Secret, "UK"));
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == pageId);
                Assert.Equal(ClassificationLevel.Secret, marking.Level);
                Assert.Equal(["UK"], marking.Countries.Select(c => c.CountryValue));
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
    /// <summary>A one-event legacy-format bundle. Bundle 1 with no predecessor by default;
    /// a FOLLOW-UP bundle for the same origin must carry the next bundle number, the
    /// previous manifest's hash and the next sequence, or the importer skips it as a
    /// duplicate (success, zero events applied) - which is silent, so a test that expects
    /// a second bundle to change anything must chain it.</summary>
    private static string WriteLegacyBundle(
        string outputDirectory, Guid spaceId, string payloadJson,
        int bundleNumber = 1, string? previousManifestHash = null, long sequence = 1)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var record = new NdjsonEventRecord("LEG", spaceId, sequence, nameof(SyncEventType.PageUpsert), payloadJson, DateTime.UtcNow);
        var ndjson = JsonSerializer.Serialize(record, jsonOptions) + Environment.NewLine;
        var ndjsonBytes = Encoding.UTF8.GetBytes(ndjson);

        var manifest = new BundleManifest(
            LowInstanceId, bundleNumber, previousManifestHash,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ndjsonBytes)),
            new Dictionary<string, SpaceEventRange> { ["LEG"] = new(spaceId, sequence, sequence, 1) },
            BundleFormat.LegacyVersion);

        var bundlePath = Path.Combine(outputDirectory, $"bundle-{bundleNumber:D6}.zip");
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

    // --- Selectors cross with the marking (design.md §21.10, format 3) ---------------------

    private static AccessRule AccessGrantWith(Guid spaceId, params SelectorValue[] selectors)
    {
        var grant = TestData.AccessGrantMirroring(EditorGrant(spaceId));
        foreach (var selector in selectors)
        {
            grant.Selectors.Add(new AccessRuleSelector { AccessRuleId = grant.Id, Category = selector.Category, Value = selector.Value });
        }

        return grant;
    }

    private static Principal UkEditor() =>
        Principal.Create("editor-sub", [], [new("nationality", ["UK"])]);

    [Fact]
    public async Task Incremental_MarkingWithSelectors_RoundTrips_AndTheHighSideEnforcesThem()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.AddRange(AccessGrantWith(space.Id, TestCatalogs.Apple, TestCatalogs.North), EditorGrant(space.Id));
        lowContext.SaveChanges();

        var created = await new PageService(lowContext, LowInstanceId).CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"), UkEditor(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);
        var set = await new PageMarkingService(lowContext, LowInstanceId).SetAsync(
            new SetPageMarkingRequest(created.Value.Id, ClassificationLevel.Secret, [], [TestCatalogs.Apple, TestCatalogs.North], UkPrefix: true),
            UkEditor(), actor.Id, AuditCtx);
        Assert.True(set.IsSuccess, set.Error?.ToString());

        var outboxEvent = Assert.Single(lowContext.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.PageMarking));
        using (var payload = JsonDocument.Parse(outboxEvent.PayloadJson))
        {
            var selectors = payload.RootElement.GetProperty("selectors");
            Assert.Equal("APPLE", selectors.GetProperty("FRUIT").GetString());
            Assert.Equal("NORTH", selectors.GetProperty("REGION").GetString());
        }

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage).ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == created.Value.Id);
                Assert.Equal([TestCatalogs.Apple, TestCatalogs.North], marking.ToMarking().Selectors);

                // The high side enforces what crossed: a reader with access but no APPLE
                // grant is refused by the grant gate, exactly as on the low side.
                highContext.AccessRules.Add(TestData.AccessGrantMirroring(EditorGrant(space.Id)));
                highContext.SaveChanges();
                var denied = Assert.IsType<ReadResult<Page>.Denied>(await new PageReadService(highContext)
                    .GetPageAsync(created.Value.Id, Principal.Create("high-user", [])));
                Assert.Equal("selector:unknown:FRUIT", denied.Reason); // the secondary database stamps no catalog: unknown, fail closed
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Baseline_CarriesEveryPagesSelectors()
    {
        var space = NewExportedSpace();
        var page = TestData.NewPage(space, "compartmented");

        using var lowContext = CreateContext();
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret).WithSelectors(TestCatalogs.Banana));
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage).ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);
                var marking = highContext.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == page.Id);
                Assert.Equal([TestCatalogs.Banana], marking.ToMarking().Selectors);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bundle_IsWrittenAsFormat3_UnderEventsV3()
    {
        // design.md §21.10: the format bump is what makes a not-yet-upgraded high side
        // refuse a selector-bearing bundle loudly (its missing-entry guard) rather than
        // absorb it with every compartment dropped.
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var lowContext = CreateContext();
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundleInfo = await new BundleExportService(lowContext, storage).ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            using var archive = System.IO.Compression.ZipFile.OpenRead(bundleInfo.BundleFilePath);
            Assert.Equal(3, BundleFormat.CurrentVersion);
            Assert.NotNull(archive.GetEntry("events.v3.ndjson"));
            Assert.Null(archive.GetEntry("events.v2.ndjson"));
            Assert.Null(archive.GetEntry("events.ndjson"));
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var manifestJson = JsonDocument.Parse(manifestStream);
            Assert.Equal(3, manifestJson.RootElement.GetProperty("formatVersion").GetInt32());

            // And every marking payload carries the selectors key, even when empty.
            using var events = new StreamReader(archive.GetEntry("events.v3.ndjson")!.Open());
            var line = events.ReadLine();
            Assert.NotNull(line);
            using var record = JsonDocument.Parse(line);
            using var payload = JsonDocument.Parse(record.RootElement.GetProperty("payloadJson").GetString()!);
            Assert.Equal(JsonValueKind.Object, payload.RootElement.GetProperty("marking").GetProperty("selectors").ValueKind);
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static string UpsertPayload(Guid pageId, Guid spaceId, object marking, string slug = "crossing") => JsonSerializer.Serialize(new
    {
        pageId,
        spaceId,
        parentPageId = (Guid?)null,
        ancestorPath = "/",
        slug,
        title = "Crossing",
        sortOrder = 0,
        content = "# Crossing",
        revisionNumber = 1,
        marking,
    });

    private static Space HighSideSpace(Guid spaceId) => new()
    {
        Id = spaceId,
        Key = "LEG",
        Name = "Legacy",
        OriginInstanceId = LowInstanceId,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Import_AnUnknownSelectorCategory_IsKeptAndMatchesNobody()
    {
        // design.md §12: a category the high side has not configured crosses verbatim and
        // is stored as it arrived - and the E gate names it unknown for every reader, so
        // the page is visible to nobody until a high-side admin configures and grants it.
        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var payload = UpsertPayload(pageId, spaceId, new
        {
            level = "OFFICIAL", eyesOnly = Array.Empty<string>(), prefix = "UK",
            selectors = new Dictionary<string, string> { ["CODEWORD"] = "zebra" },
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
                highContext.Spaces.Add(HighSideSpace(spaceId));
                highContext.AccessRules.Add(TestData.AccessGrantMirroring(EditorGrant(spaceId)));
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage).ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == pageId);
                Assert.Equal([new SelectorValue("CODEWORD", "ZEBRA")], marking.ToMarking().Selectors); // canonicalized, kept verbatim otherwise

                var denied = Assert.IsType<ReadResult<Page>.Denied>(await new PageReadService(highContext)
                    .GetPageAsync(pageId, Principal.Create("high-user", [])));
                Assert.Equal("selector:unknown:CODEWORD", denied.Reason);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_AMalformedSelectorsObject_LandsANewPageUnavailable_AndLeavesAnExistingRowAlone()
    {
        // design.md §21.10: anything malformed in selectors makes the WHOLE marking
        // unparseable - dropping only the bad selector would widen - so a new page fails
        // closed to UNKNOWN (unavailable, readable by nobody; it still renders as the
        // sentinel's TOP SECRET) and an existing row is not re-classified from silence.
        var newPageId = Guid.CreateVersion7();
        var existingPageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var malformed = new { level = "OFFICIAL", eyesOnly = Array.Empty<string>(), prefix = "UK", selectors = new[] { "APPLE" } };

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var space = HighSideSpace(spaceId);
                var existing = TestData.NewPage(space, "existing");
                existing.Id = existingPageId;
                highContext.Spaces.Add(space);
                highContext.Pages.Add(existing);
                highContext.PageMarkings.Add(TestData.NewMarking(existing, ClassificationLevel.Secret, "UK").WithSelectors(TestCatalogs.Apple));
                // Access for everyone, so the denial below is the marking's and not the space's.
                highContext.AccessRules.Add(TestData.AccessGrantMirroring(EditorGrant(spaceId)));
                highContext.SaveChanges();

                var newBundle = WriteLegacyBundle(Path.Combine(outputDir, "new"), spaceId, UpsertPayload(newPageId, spaceId, malformed));
                Assert.True((await new BundleImportService(highContext, storage).ImportAsync(newBundle, LowInstanceId, AuditCtx)).IsSuccess);
                var landed = highContext.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == newPageId);
                Assert.True(landed.IsUnavailable);
                Assert.Equal(ClassificationLevel.TopSecret, landed.Level);
                Assert.Empty(landed.Selectors);
                Assert.Equal(
                    "marking:unavailable",
                    Assert.IsType<ReadResult<Page>.Denied>(
                        await new PageReadService(highContext).GetPageAsync(newPageId, EveryoneAdmittingPrincipal())).Reason);

                // Chained as bundle 2: an unchained repeat of bundle 1 is skipped as a
                // duplicate (success, nothing applied), and "left alone" would then be
                // vacuous. EventsApplied == 1 proves the upsert really ran against the row.
                var previousHash = highContext.SyncImportStates.Single(s => s.OriginInstanceId == LowInstanceId).LastManifestHash;
                var existingBundle = WriteLegacyBundle(Path.Combine(outputDir, "existing"), spaceId, UpsertPayload(existingPageId, spaceId, malformed, slug: "existing"),
                    bundleNumber: 2, previousManifestHash: previousHash, sequence: 2);
                var existingResult = await new BundleImportService(highContext, storage).ImportAsync(existingBundle, LowInstanceId, AuditCtx);
                Assert.True(existingResult.IsSuccess);
                Assert.Equal(1, existingResult.Value.EventsApplied);
                var untouched = highContext.PageMarkings.Include(m => m.Selectors).Include(m => m.Countries).Single(m => m.PageId == existingPageId);
                Assert.Equal(ClassificationLevel.Secret, untouched.Level);
                Assert.Equal([TestCatalogs.Apple], untouched.ToMarking().Selectors);
                Assert.Equal(["UK"], untouched.Countries.Select(c => c.CountryValue));
                Assert.False(untouched.IsUnavailable); // silence never re-classifies an existing row, in either direction
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_APreSelectorPayload_LandsWithNoSelectors()
    {
        // A format-2 marking: level, caveat, prefix, and no selectors key at all. It said
        // nothing about compartments, so it lands with none - never an invented one, and
        // never a refusal of a bundle already sitting on a transfer disk.
        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var payload = UpsertPayload(pageId, spaceId, new { level = "SECRET", eyesOnly = new[] { "UK" }, prefix = "UK" });

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundlePath = WriteLegacyBundle(outputDir, spaceId, payload);
            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                highContext.Spaces.Add(HighSideSpace(spaceId));
                highContext.SaveChanges();

                Assert.True((await new BundleImportService(highContext, storage).ImportAsync(bundlePath, LowInstanceId, AuditCtx)).IsSuccess);

                var marking = highContext.PageMarkings.Include(m => m.Selectors).Include(m => m.Countries).Single(m => m.PageId == pageId);
                Assert.Equal(ClassificationLevel.Secret, marking.Level);
                Assert.Empty(marking.Selectors);
                Assert.Equal("UK SECRET UK EYES ONLY", marking.ToMarking().Format(SelectorCatalog.Empty));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
}
