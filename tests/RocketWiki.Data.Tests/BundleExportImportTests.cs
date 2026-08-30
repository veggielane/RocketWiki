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
/// design.md §12: bundle export/import round-tripped between two genuinely separate
/// SQLite databases (simulating low and high), which this feature's provider-agnostic
/// zip/JSON file format makes fully testable end-to-end - unlike SearchService's
/// SQL-Server-only FTS path, nothing here is unexercised.
///
/// Bundles are format 2 (BundleFormat): baselines carry each page's full PageRevision
/// history and incremental PageUpserts carry the one revision they correspond to, with
/// revision authors arriving as shadow users. The format-versioning tests at the bottom
/// pin the era contract: a legacy format-1 bundle still imports (current-state-only), a
/// future format is refused loudly.
/// </summary>
public class BundleExportImportTests : SqliteTestBase
{
    private const string LowInstanceId = "low-instance";

    /// <summary>CreateContext() builds the "low" side here, whose exported spaces
    /// carry OriginInstanceId = LowInstanceId — the outbox writer only journals when
    /// the context's own instance id matches (design.md §12). The "high" side
    /// (CreateSecondaryDatabase) deliberately configures none: import raises no
    /// sync-relevant events, so ownership is never checked there.</summary>
    protected override string DefaultLocalInstanceId => LowInstanceId;
    private static readonly AuditContext AuditCtx = new(AuditChannel.Sync, "sync-job-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("editor-sub", groups);

    /// <summary>An editor who also holds a clearance. Needed wherever a test raises a
    /// marking: §21.6 refuses one you could not then read, and the plain EditorPrincipal
    /// holds no clearance attribute at all — so it can only ever write OFFICIAL.</summary>
    private static Principal ClearedEditorPrincipal(string clearance, params string[] nationalities) =>
        Principal.Create("editor-sub", [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["clearance"] = [clearance],
            ["nationality"] = nationalities,
        });

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

    /// <summary>SpaceAdmin outranks Editor (EffectivePermissionCalculator: role >= Editor can edit), and AccessRuleService.CreateAsync requires exactly SpaceAdmin - so this one grant covers both page mutation and restriction management for tests that need to create a PageRestriction.</summary>
    private static AccessRule SpaceAdminGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.SpaceAdmin,
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
        tempDir = Path.Combine(Path.GetTempPath(), "rocketwiki-sync-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new FileStorageOptions { FileSystem = new FileSystemFileStorageOptions { Root = tempDir } });
        return new FileSystemFileStorage(options);
    }

    private static string CreateBundleOutputDir() =>
        Path.Combine(Path.GetTempPath(), "rocketwiki-bundles-tests", Guid.NewGuid().ToString("N"));

    // --- Baseline export/import -------------------------------------------------------

    [Fact]
    public async Task Baseline_ExportThenImport_RecreatesPagesOnTheOtherSide()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space, "home");
        page.Title = "Home";
        page.CurrentContent = "# Welcome";

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            Assert.Equal(1, bundleInfo.BundleNumber);
            Assert.True(File.Exists(bundleInfo.BundleFilePath));

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.True(result.IsSuccess);
                var importedPage = highContext.Pages.Single(p => p.Id == page.Id);
                Assert.Equal("Home", importedPage.Title);
                Assert.Equal("# Welcome", importedPage.CurrentContent);
                Assert.Equal(space.Id, importedPage.SpaceId);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// design.md §12 calls a baseline a "full snapshot", and for a long time it was not:
    /// it carried pages and entries and nothing else. A space flagged for export after it
    /// already had content therefore delivered that content stripped of everything §12's
    /// "what travels" table says travels with it.
    ///
    /// <para><b>Page restrictions are the reason this test exists at the baseline tier
    /// rather than only the incremental one.</b> Every other omission was a completeness
    /// bug; a missing restriction is fail-OPEN — the page lands on the high side readable
    /// by every viewer of the replica, which is the one direction §12 never takes
    /// anywhere else. The rest are asserted alongside it because they were omitted by the
    /// same line of code and would be re-omitted by the same edit.</para>
    ///
    /// <para>Everything here is built through the real services, so the baseline is
    /// compared against content shaped exactly as a running instance would have shaped
    /// it rather than against hand-inserted rows.</para>
    /// </summary>
    [Fact]
    public async Task Baseline_CarriesRestrictionsCommentsAttachmentsLabelsAndProperties()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        // In the engineering group, because the restriction created below gates canView
        // - and canView is what commenting, labelling and uploading all require (§6.4.2).
        var author = EditorPrincipal("engineering");

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(SpaceAdminGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var page = (await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "runbook", "Runbook", "# Runbook"),
            author, actor.Id, AuditCtx)).Value;

        // The restriction: the fail-open one if it does not cross.
        var restriction = await new AccessRuleService(lowContext).CreateAsync(
            new CreateAccessRuleRequest(
                AccessRuleKind.PageRestriction, null, page.Id, null, PageAction.View,
                """{ "group": "engineering" }"""),
            author, isInstanceAdmin: false, actor.Id, AuditCtx);
        Assert.True(restriction.IsSuccess, $"restriction failed: {restriction.Error}");

        var commentService = new CommentService(lowContext, LowInstanceId);
        var parent = (await commentService.AddCommentAsync(
            new AddCommentRequest(page.Id, null, "Parent comment"), author, actor.Id, AuditCtx)).Value;
        var reply = (await commentService.AddCommentAsync(
            new AddCommentRequest(page.Id, parent.Id, "Reply"), author, actor.Id, AuditCtx)).Value;
        // A tombstoned parent still has to cross, or the live reply's ParentCommentId FK
        // would point at nothing on the high side.
        Assert.True((await commentService.DeleteCommentAsync(
            new DeleteCommentRequest(parent.Id), author, actor.Id, AuditCtx)).IsSuccess);

        var labelService = new LabelService(lowContext, LowInstanceId);
        var label = (await labelService.CreateLabelAsync(
            new CreateLabelRequest(space.Id, "runbooks"), author, actor.Id, AuditCtx)).Value;
        Assert.True((await labelService.AttachLabelAsync(
            new AttachLabelRequest(page.Id, label.Id), author, actor.Id, AuditCtx)).IsSuccess);

        var propertyService = new PagePropertyService(lowContext, LowInstanceId);
        var key = (await propertyService.CreateKeyAsync(
            new CreatePagePropertyKeyRequest("Status", null), isInstanceAdmin: true, actor.Id, AuditCtx)).Value;
        Assert.True((await propertyService.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "Draft"), author, actor.Id, AuditCtx)).IsSuccess);

        var lowStorage = CreateFileStorage(out var lowStorageDir);
        var highStorage = CreateFileStorage(out var highStorageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var uploaded = (await new AttachmentService(lowContext, lowStorage, LowInstanceId).UploadAsync(
                new UploadAttachmentRequest(page.Id, "diagram.png", "image/png", new MemoryStream("blob-bytes"u8.ToArray())),
                author, actor.Id, AuditCtx)).Value;

            var bundleInfo = await new BundleExportService(lowContext, lowStorage)
                .ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var result = await new BundleImportService(highContext, highStorage)
                    .ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess, $"import failed: {result.Error}");

                // The restriction arrived, with the expression intact - a page restricted
                // on low is restricted on high (§12), not merely present.
                var landed = Assert.Single(highContext.AccessRules
                    .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId == page.Id));
                Assert.Equal(PageAction.View, landed.Action);
                Assert.Contains("engineering", landed.ExpressionJson);

                // Thread shape survives: the tombstoned parent crossed so its reply has
                // something to hang off.
                Assert.True(highContext.Comments.Single(c => c.Id == parent.Id).IsDeleted);
                Assert.Equal(parent.Id, highContext.Comments.Single(c => c.Id == reply.Id).ParentCommentId);
                Assert.Equal("Reply", highContext.Comments.Single(c => c.Id == reply.Id).Body);

                // Label by name, property by normalized key name - both materialized on
                // the high side from the payload, never by id.
                Assert.Equal(
                    "runbooks",
                    highContext.PageLabels.Include(pl => pl.Label).Single(pl => pl.PageId == page.Id).Label!.Name);
                var property = highContext.PageProperties.Include(p => p.PropertyKey).Single(p => p.PageId == page.Id);
                Assert.Equal("Status", property.PropertyKey!.Key);
                Assert.Equal("Draft", property.Value);

                // And the attachment, bytes included - WriteBlobsAsync keys off
                // Attachment lines, so a baseline that emitted none shipped no blobs either.
                var importedAttachment = highContext.Attachments.Single(a => a.Id == uploaded.Id);
                Assert.Equal("diagram.png", importedAttachment.FileName);
                await using var readBack = await highStorage.OpenReadAsync(importedAttachment.StorageKey, CancellationToken.None);
                using var reader = new StreamReader(readBack);
                Assert.Equal("blob-bytes", await reader.ReadToEndAsync());
            }
        }
        finally
        {
            if (Directory.Exists(lowStorageDir)) Directory.Delete(lowStorageDir, recursive: true);
            if (Directory.Exists(highStorageDir)) Directory.Delete(highStorageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// design.md §12: a bundle claiming to originate from THIS instance is refused before
    /// anything is applied. Landing it would write the spaces with
    /// <c>OriginInstanceId</c> equal to the local id — the exact test
    /// <c>Space.IsReplicaOf</c> uses — so "replicas are read-only, always" would answer
    /// false and every mirrored space would be writable.
    ///
    /// <para>The realistic route in is a configuration mistake, not tampering: the Helm
    /// chart shipped <c>instanceId: ""</c>, which omitted the env var and let both sides
    /// fall back to "standalone". The chart now requires the value; this refuses the
    /// consequence for every deployment path, including the two the chart does not cover.</para>
    /// </summary>
    [Fact]
    public async Task Import_OfABundleFromThisVeryInstance_IsRefused_AndNothingLands()
    {
        var space = NewExportedSpace();
        var page = TestData.NewPage(space, "home");

        using var lowContext = CreateContext();
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
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
                // The importing instance's id is the SAME as the bundle's origin - two
                // instances left on one identity.
                var result = await new BundleImportService(highContext, storage, LowInstanceId)
                    .ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.False(result.IsSuccess);
                Assert.Equal(LowInstanceId, Assert.IsType<BundleSelfOriginError>(result.Error).InstanceId);

                // Refused before anything is read, so nothing landed - not the space, not
                // the page, and no import state to make the next attempt look like a gap.
                Assert.Empty(highContext.Pages.IgnoreQueryFilters().ToList());
                Assert.Empty(highContext.Spaces.IgnoreQueryFilters().ToList());
                Assert.Empty(highContext.SyncImportStates.ToList());
            }

            // Non-vacuous: the same bundle into a genuinely different instance applies.
            var (okConnection, okContext) = CreateSecondaryDatabase();
            using (okConnection)
            using (okContext)
            {
                var ok = await new BundleImportService(okContext, storage, "high-instance")
                    .ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.True(ok.IsSuccess, $"import into a distinct instance failed: {ok.Error}");
                Assert.Single(okContext.Pages.ToList());
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Baseline_CarriesThePageIcon_ByWireName()
    {
        var space = NewExportedSpace();
        var decorated = TestData.NewPage(space, "decorated");
        decorated.Icon = PageIcon.Rocket;
        var plain = TestData.NewPage(space, "plain");

        using var lowContext = CreateContext();
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(decorated);
        lowContext.Pages.Add(plain);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                Assert.True((await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                Assert.Equal(PageIcon.Rocket, highContext.Pages.Single(p => p.Id == decorated.Id).Icon);
                Assert.Null(highContext.Pages.Single(p => p.Id == plain.Id).Icon);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// The incremental half, which the baseline test above cannot stand in for: the two
    /// sides serialize a PageUpsert from different code (BundleExportService for a
    /// baseline, SyncOutboxWriter for a journalled event). Both directions are asserted
    /// in one round trip because the second is what makes the first load-bearing — the
    /// import side reads an absent `icon` key as "no icon", so a payload that simply
    /// omitted the field would clear an icon the replica already held rather than
    /// merely failing to deliver a new one.
    /// </summary>
    [Fact]
    public async Task Incremental_SettingAndClearingAnIcon_BothReachTheReplica()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome", PageIcon.Rocket),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var first = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(first);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                Assert.True((await importService.ImportAsync(first!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);
                Assert.Equal(PageIcon.Rocket, highContext.Pages.Single(p => p.Id == created.Value.Id).Icon);

                // A later edit that clears the icon must land as a clear on the replica -
                // and an edit that changed only the content must not clear it by accident,
                // which is the same payload key doing both jobs.
                var cleared = await pageService.UpdatePageContentAsync(
                    new UpdatePageContentRequest(created.Value.Id, 1, "Home", "# Edited", null, Icon: null),
                    EditorPrincipal(), actor.Id, AuditCtx);
                Assert.True(cleared.IsSuccess);

                var second = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(second);
                Assert.True((await importService.ImportAsync(second!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var replicated = highContext.Pages.Single(p => p.Id == created.Value.Id);
                Assert.Equal("# Edited", replicated.CurrentContent);
                Assert.Null(replicated.Icon);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// docs/ENTRIES-AND-FORMS-PLAN.md: entries are a page's structured content, so they
    /// cross with it — including their own markings, which is the whole reason they are
    /// worth syncing rather than leaving local.
    ///
    /// <para>Both directions in one round trip, because the second is what makes the
    /// first load-bearing: the import assigns every field it reads, so a payload that
    /// merely FAILED to carry a change would be indistinguishable from one that cleared
    /// it. That is the shape of the bug page icons shipped with.</para>
    /// </summary>
    [Fact]
    public async Task Incremental_AnEntryAndItsMarking_BothReachTheReplica()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page.IsSuccess);

        var entryService = new PageEntryService(lowContext, LowInstanceId);
        var entry = await entryService.CreateAsync(
            new CreatePageEntryRequest(page.Value.Id, "incident-report", """{"severity":"high"}"""),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(entry.IsSuccess, $"{entry.Error}");

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var first = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(first);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                Assert.True((await importService.ImportAsync(first!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var landed = highContext.PageEntries.Include(e => e.Countries).Single(e => e.Id == entry.Value.Id);
                Assert.Equal("incident-report", landed.Collection);
                Assert.Equal("""{"severity":"high"}""", landed.Data);
                Assert.Equal(ClassificationLevel.Official, landed.Level);

                // Raising the marking and editing the data at once: both must land, and
                // neither may be silently reset by the other travelling in the same payload.
                var raised = await entryService.UpdateAsync(
                    new UpdatePageEntryRequest(entry.Value.Id, 1, """{"severity":"critical"}""",
                        ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], "UK")),
                    ClearedEditorPrincipal("SECRET", "UK"), actor.Id, AuditCtx);
                Assert.True(raised.IsSuccess, $"{raised.Error}");

                var second = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(second);
                Assert.True((await importService.ImportAsync(second!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var updated = highContext.PageEntries.Include(e => e.Countries).Single(e => e.Id == entry.Value.Id);
                Assert.Equal("""{"severity":"critical"}""", updated.Data);
                Assert.Equal(ClassificationLevel.Secret, updated.Level);
                Assert.Equal("UK", Assert.Single(updated.Countries).CountryValue);

                // And a delete crosses as a tombstone rather than as an absence — an entry
                // that simply stopped being mentioned would live forever on the replica.
                // Deleting it needs the clearance too: an entry you cannot read is an
                // entry you cannot remove.
                Assert.True((await entryService.DeleteAsync(
                    new DeletePageEntryRequest(entry.Value.Id, 2), ClearedEditorPrincipal("SECRET", "UK"), actor.Id, AuditCtx)).IsSuccess);

                var third = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(third);
                Assert.True((await importService.ImportAsync(third!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                Assert.True(highContext.PageEntries.IgnoreQueryFilters().Single(e => e.Id == entry.Value.Id).IsDeleted);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// An entry arriving with no declared marking lands at TOP SECRET, not OFFICIAL —
    /// the same fail-closed reading a page gets (§21). Content from a lower instance
    /// without a classification is exactly the case where guessing the bottom of the
    /// ladder would be a cross-boundary disclosure.
    /// </summary>
    [Fact]
    public async Task Import_AnEntryWithNoMarking_LandsAtTopSecret()
    {
        var webOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var entryId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            entryId,
            pageId,
            collection = "incident-report",
            data = "{}",
            version = 1,
            isDeleted = false,
            // No level, no eyesOnly, no prefix - a bundle written before entries were
            // marked, or by a build that got it wrong.
        }, webOptions);
        // The page travels first: an entry never arrives without one, and the foreign key
        // would refuse it anyway. Marked here, so only the ENTRY's missing marking is
        // under test rather than the page's.
        var pagePayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            pageId,
            spaceId,
            parentPageId = (Guid?)null,
            ancestorPath = "/",
            slug = "home",
            title = "Home",
            sortOrder = 0,
            content = "# Home",
            revisionNumber = 1,
            marking = new { level = "OFFICIAL", eyesOnly = Array.Empty<string>(), prefix = "UK" },
        }, webOptions);
        var ndjson =
            System.Text.Json.JsonSerializer.Serialize(
                new NdjsonEventRecord("FUT", spaceId, 1, nameof(SyncEventType.PageUpsert), pagePayload, DateTime.UtcNow), webOptions) + "\n"
            + System.Text.Json.JsonSerializer.Serialize(
                new NdjsonEventRecord("FUT", spaceId, 2, nameof(SyncEventType.PageEntry), payload, DateTime.UtcNow), webOptions) + "\n";
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ndjson)));

        var outputDir = CreateBundleOutputDir();
        Directory.CreateDirectory(outputDir);
        var storage = CreateFileStorage(out var storageDir);
        try
        {
            var bundlePath = Path.Combine(outputDir, "bundle-000001.zip");
            WriteRawBundle(bundlePath, new
            {
                instanceId = LowInstanceId,
                bundleNumber = 1,
                previousManifestHash = (string?)null,
                payloadSha256 = payloadHash,
                spaceEventRanges = new Dictionary<string, object>
                {
                    ["FUT"] = new { spaceId, fromSequence = 1, toSequence = 2, eventCount = 2 },
                },
                formatVersion = BundleFormat.CurrentVersion,
            }, BundleFormat.EventsEntryName(BundleFormat.CurrentVersion), ndjson);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var result = await new BundleImportService(highContext, storage)
                    .ImportAsync(bundlePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess, $"{result.Error}");
                Assert.Equal(ClassificationLevel.TopSecret,
                    highContext.PageEntries.Single(e => e.Id == entryId).Level);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// design.md §12's decoration-degrades rule, at the import boundary: a bundle from a
    /// newer instance naming an icon this build has never heard of imports the page
    /// without one. Refusing the bundle instead would strand every other change in it
    /// behind a decoration. The SPA's picker keeps an unrecognised icon selected on the
    /// strength of this, so it is a cross-component contract rather than an internal
    /// nicety.
    /// </summary>
    [Fact]
    public async Task Import_AnUnknownIconName_LandsThePageWithoutAnIcon_NotAnError()
    {
        var webOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var pageId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            pageId,
            spaceId,
            parentPageId = (Guid?)null,
            ancestorPath = "/",
            slug = "from-the-future",
            title = "From The Future",
            icon = "SPACE_ELEVATOR", // no such member in this build's PageIcon
            sortOrder = 0,
            content = "# Newer",
            revisionNumber = 1,
        }, webOptions);
        var ndjson = System.Text.Json.JsonSerializer.Serialize(
            new NdjsonEventRecord("FUT", spaceId, 1, nameof(SyncEventType.PageUpsert), payload, DateTime.UtcNow), webOptions) + "\n";
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ndjson)));

        var outputDir = CreateBundleOutputDir();
        Directory.CreateDirectory(outputDir);
        var storage = CreateFileStorage(out var storageDir);
        try
        {
            var bundlePath = Path.Combine(outputDir, "bundle-000001.zip");
            WriteRawBundle(bundlePath, new
            {
                instanceId = LowInstanceId,
                bundleNumber = 1,
                previousManifestHash = (string?)null,
                payloadSha256 = payloadHash,
                spaceEventRanges = new Dictionary<string, object>
                {
                    ["FUT"] = new { spaceId, fromSequence = 1, toSequence = 1, eventCount = 1 },
                },
                formatVersion = BundleFormat.CurrentVersion,
            }, BundleFormat.EventsEntryName(BundleFormat.CurrentVersion), ndjson);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundlePath, LowInstanceId, AuditCtx);

                Assert.True(result.IsSuccess);
                var page = highContext.Pages.Single(p => p.Id == pageId);
                Assert.Equal("From The Future", page.Title);
                Assert.Equal("# Newer", page.CurrentContent); // the rest of the payload still lands
                Assert.Null(page.Icon);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- Incremental export/import: the full mixed-event round trip -------------------

    [Fact]
    public async Task Incremental_ExportThenImport_AppliesPageCommentAndRestrictionEvents()
    {
        var actor = TestData.NewUser();
        actor.DisplayName = "Alice Author";
        actor.Email = "alice@example.com";
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(SpaceAdminGrant(space.Id)); // needed to create the PageRestriction below, not just edit
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var commentService = new CommentService(lowContext, LowInstanceId);
        var accessRuleService = new AccessRuleService(lowContext);

        var createdPage = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Welcome"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(createdPage.IsSuccess);

        var addedComment = await commentService.AddCommentAsync(
            new AddCommentRequest(createdPage.Value.Id, null, "Nice page!"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(addedComment.IsSuccess);

        var restriction = await accessRuleService.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.PageRestriction, null, createdPage.Value.Id, null, PageAction.View, """{ "group": "top-secret" }"""),
            EditorPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);
        Assert.True(restriction.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);
            Assert.Equal(1, bundleInfo!.BundleNumber);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess);

                var importedPage = highContext.Pages.Single(p => p.Id == createdPage.Value.Id);
                Assert.Equal("Home", importedPage.Title);

                var importedComment = highContext.Comments.Single(c => c.PageId == createdPage.Value.Id);
                Assert.Equal("Nice page!", importedComment.Body);

                // design.md §12: the author arrives as a shadow user, never loginable.
                var shadowAuthor = highContext.Users.Single(u => u.Id == actor.Id);
                Assert.True(shadowAuthor.IsExternal);
                Assert.Null(shadowAuthor.Subject);
                Assert.Equal("Alice Author", shadowAuthor.DisplayName);
                Assert.Equal("alice@example.com", shadowAuthor.Email);

                var importedRestriction = highContext.AccessRules.Single(r => r.PageId == createdPage.Value.Id);
                Assert.Equal(AccessRuleKind.PageRestriction, importedRestriction.Kind);
                Assert.Equal(PageAction.View, importedRestriction.Action);
                Assert.Contains("top-secret", importedRestriction.ExpressionJson);

                Assert.Contains(highContext.AuditEvents, e => e.Action == "sync.import");
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Incremental_PageMoveDeleteRestore_ApplyCorrectlyOnImport()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var parent = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "parent", "Parent", "# P"), EditorPrincipal(), actor.Id, AuditCtx);
        var other = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "other", "Other", "# O"), EditorPrincipal(), actor.Id, AuditCtx);
        var page = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "page", "Page", "# X"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(parent.IsSuccess && other.IsSuccess && page.IsSuccess);

        var moved = await pageService.MovePageAsync(new MovePageRequest(page.Value.Id, parent.Value.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(moved.IsSuccess);
        var deleted = await pageService.DeletePageAsync(new DeletePageRequest(page.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(deleted.IsSuccess);
        var restored = await pageService.RestorePageAsync(new RestorePageRequest(page.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(restored.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess);

                var importedPage = highContext.Pages.Single(p => p.Id == page.Value.Id);
                Assert.Equal(parent.Value.Id, importedPage.ParentPageId);
                Assert.False(importedPage.IsDeleted); // deleted then restored - ends up live
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Incremental_AttachmentEvent_TransfersBlobBytesToTheOtherSidesStorage()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        // Created via PageService (not a raw Add) so a PageUpsert sync event actually
        // exists to create the page on the high side - a directly-inserted Page has no
        // domain event behind it, and the attachment's PageId would FK into nothing.
        var pageService = new PageService(lowContext, LowInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "diagram-page", "Diagram Page", "# Diagrams"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page.IsSuccess);

        var lowStorage = CreateFileStorage(out var lowStorageDir);
        var highStorage = CreateFileStorage(out var highStorageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var attachmentService = new AttachmentService(lowContext, lowStorage, LowInstanceId);
            var uploaded = await attachmentService.UploadAsync(
                new UploadAttachmentRequest(page.Value.Id, "diagram.png", "image/png", new MemoryStream("blob-bytes"u8.ToArray())),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            var exportService = new BundleExportService(lowContext, lowStorage);
            var bundleInfo = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, highStorage);
                var result = await importService.ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess);

                var importedAttachment = highContext.Attachments.Single(a => a.Id == uploaded.Value.Id);
                Assert.Equal(uploaded.Value.ContentHash, importedAttachment.ContentHash);
                Assert.NotEqual(uploaded.Value.StorageKey, importedAttachment.StorageKey); // a fresh, local-to-high key

                await using var readBack = await highStorage.OpenReadAsync(importedAttachment.StorageKey, CancellationToken.None);
                using var reader = new StreamReader(readBack);
                Assert.Equal("blob-bytes", await reader.ReadToEndAsync());
            }
        }
        finally
        {
            if (Directory.Exists(lowStorageDir)) Directory.Delete(lowStorageDir, recursive: true);
            if (Directory.Exists(highStorageDir)) Directory.Delete(highStorageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Incremental_LabelAttachThenDetach_AppliesOnImport()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        // Created via PageService (not a raw Add) so a PageUpsert sync event actually
        // exists to create the pages on the high side - see the identical note in the
        // attachment round-trip test above.
        var pageService = new PageService(lowContext, LowInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "how-to-page", "How-To Page", "# How To"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page.IsSuccess);
        var otherPage = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "other-page", "Other Page", "# Other"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(otherPage.IsSuccess);

        var labelService = new LabelService(lowContext, LowInstanceId);
        var label = await labelService.CreateLabelAsync(new CreateLabelRequest(space.Id, "how-to"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(label.IsSuccess);

        // Attach to both pages, then detach from just one - proves the import side
        // applies "attach" and "detach" as distinct events, not just a final snapshot.
        var attached = await labelService.AttachLabelAsync(new AttachLabelRequest(page.Value.Id, label.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(attached.IsSuccess);
        var attachedOther = await labelService.AttachLabelAsync(new AttachLabelRequest(otherPage.Value.Id, label.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(attachedOther.IsSuccess);
        var detached = await labelService.DetachLabelAsync(new DetachLabelRequest(otherPage.Value.Id, label.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(detached.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess);

                var importedLabel = highContext.Labels.Single(l => l.Name == "how-to" && l.SpaceId == space.Id);
                Assert.True(highContext.PageLabels.Any(pl => pl.PageId == page.Value.Id && pl.LabelId == importedLabel.Id));
                Assert.False(highContext.PageLabels.Any(pl => pl.PageId == otherPage.Value.Id && pl.LabelId == importedLabel.Id));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Incremental_PagePropertySetThenRemove_AppliesOnImport_MaterializingTheRegistryKeyByName()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "spec", "Spec", "# Spec"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page.IsSuccess);
        var otherPage = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "other", "Other", "# Other"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(otherPage.IsSuccess);

        var propertyService = new PagePropertyService(lowContext, LowInstanceId);
        var key = await propertyService.CreateKeyAsync(
            new CreatePagePropertyKeyRequest("Owner", "Who owns this page"), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.True(key.IsSuccess);

        // Set on both pages, then remove from one - proves the import side applies "set"
        // and "remove" as distinct events, not just a final snapshot.
        Assert.True((await propertyService.SetAsync(
            new SetPagePropertyRequest(page.Value.Id, key.Value.Id, "Ada Lovelace"), EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);
        Assert.True((await propertyService.SetAsync(
            new SetPagePropertyRequest(otherPage.Value.Id, key.Value.Id, "Grace Hopper"), EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);
        Assert.True((await propertyService.RemoveAsync(
            new RemovePagePropertyRequest(otherPage.Value.Id, key.Value.Id), EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                // The high side has NEVER seen this registry key: its property-key table
                // starts empty, and the payload carries only the key's name (design.md
                // §20). Import must materialize it or the value would reference nothing.
                Assert.Empty(highContext.PagePropertyKeys);

                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result.IsSuccess);

                var importedKey = highContext.PagePropertyKeys.Single(k => k.KeyNormalized == "owner");
                Assert.Equal("Owner", importedKey.Key);
                Assert.Null(importedKey.CreatedByUserId); // no local actor for a sync-materialized key

                var applied = highContext.PageProperties.Single(p => p.PageId == page.Value.Id);
                Assert.Equal(importedKey.Id, applied.PagePropertyKeyId);
                Assert.Equal("Ada Lovelace", applied.Value);
                Assert.Null(applied.UpdatedByUserId);

                Assert.False(highContext.PageProperties.Any(p => p.PageId == otherPage.Value.Id));

                // Idempotent: re-importing the same bundle is a no-op, not a duplicate
                // key row or a resurrected value.
                var second = await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(second.IsSuccess);
                Assert.Single(highContext.PagePropertyKeys.ToList());
                Assert.Single(highContext.PageProperties.ToList());
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- Gap-refusing, idempotent, chain-verified -------------------------------------

    /// <summary>
    /// A REFUSED bundle must leave no trace in storage either, not just in the database.
    ///
    /// <para>The class doc claims "every check that can fail happens BEFORE any entity in
    /// the bundle is applied, so a rejected bundle never partially lands" — true of rows,
    /// false of blobs. The per-space sequence check used to run inside the apply loop, so
    /// a gap in the SECOND space returned only after the first space's Attachment events
    /// had already called <c>SaveAsync</c>. <c>SaveChangesAsync</c> is never reached on a
    /// refusal, so the rows evaporate and the bytes do not — and re-importing the
    /// corrected bundle mints a fresh storage key, making the orphan permanent with no
    /// janitor to collect it.</para>
    ///
    /// <para>Two spaces, deliberately: with one space the gap is found on its first record
    /// and nothing has been written yet, which is why this went unnoticed.</para>
    /// </summary>
    [Fact]
    public async Task Import_RefusedForAGapInOneSpace_LeavesNoBlobsFromTheOther()
    {
        var actor = TestData.NewUser();
        var goodSpace = NewExportedSpace("GOOD");
        var gappedSpace = NewExportedSpace("GAPPED");

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.AddRange(goodSpace, gappedSpace);
        lowContext.AccessRules.Add(EditorGrant(goodSpace.Id));
        lowContext.SaveChanges();

        var lowStorage = CreateFileStorage(out var lowStorageDir);
        var highStorage = CreateFileStorage(out var highStorageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            // An attachment in the FIRST space: its bytes are what must not survive.
            var page = (await new PageService(lowContext, LowInstanceId).CreatePageAsync(
                new CreatePageRequest(goodSpace.Id, null, "with-attachment", "With Attachment", "# A"),
                EditorPrincipal(), actor.Id, AuditCtx)).Value;
            Assert.True((await new AttachmentService(lowContext, lowStorage, LowInstanceId).UploadAsync(
                new UploadAttachmentRequest(page.Id, "spec.bin", "application/octet-stream",
                    new MemoryStream("orphan-bytes"u8.ToArray())),
                EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

            // A second space whose outbox sequence starts at 5 rather than 1 — the gap.
            gappedSpace.LastOutboxSequence = 4;
            lowContext.SyncOutboxEvents.Add(new SyncOutboxEvent
            {
                SpaceId = gappedSpace.Id,
                SequenceNumber = 5,
                EventType = SyncEventType.PageDelete,
                PayloadJson = """{"rootPageId":"00000000-0000-0000-0000-000000000001","pageIds":[]}""",
                CreatedAtUtc = DateTime.UtcNow,
            });
            lowContext.SaveChanges();

            var bundleInfo = await new BundleExportService(lowContext, lowStorage)
                .ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundleInfo);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var result = await new BundleImportService(highContext, highStorage, "high-instance")
                    .ImportAsync(bundleInfo!.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.False(result.IsSuccess);
                Assert.IsType<SpaceSequenceGapError>(result.Error);

                // Rows: none, as always.
                Assert.Empty(highContext.Attachments.IgnoreQueryFilters().ToList());

                // Bytes: none either, which is the half that used to leak.
                Assert.False(
                    Directory.Exists(highStorageDir) && Directory.EnumerateFiles(highStorageDir, "*", SearchOption.AllDirectories).Any(),
                    "a refused bundle wrote blobs to the high side's storage; they are unreferenced and permanent.");
            }
        }
        finally
        {
            if (Directory.Exists(lowStorageDir)) Directory.Delete(lowStorageDir, recursive: true);
            if (Directory.Exists(highStorageDir)) Directory.Delete(highStorageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_BundleAheadOfExpected_ReturnsBundleGapError()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId); // real bundle 1, never imported

            // A genuine bundle 2: its manifest legitimately declares BundleNumber=2 - the
            // gap check operates on that declared number, not on the file's name, so
            // simply copying bundle 1's bytes under a "bundle-000002.zip" name (as an
            // earlier version of this test did) doesn't produce an ahead-of-expected
            // bundle at all - it just re-delivers bundle 1 under a different filename,
            // which the idempotency rule correctly accepts.
            var pageService = new PageService(lowContext, LowInstanceId);
            var secondPage = await pageService.CreatePageAsync(
                new CreatePageRequest(space.Id, null, "second", "Second", "# Second"), EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(secondPage.IsSuccess);
            var bundle2Info = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundle2Info);
            Assert.Equal(2, bundle2Info!.BundleNumber);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundle2Info.BundleFilePath, LowInstanceId, AuditCtx); // bundle 1 skipped

                Assert.False(result.IsSuccess);
                var gapError = Assert.IsType<BundleGapError>(result.Error);
                Assert.Equal(1, gapError.ExpectedBundleNumber);
                Assert.Equal(2, gapError.ActualBundleNumber);
                Assert.Empty(highContext.Pages); // nothing partially applied
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_SameBundleTwice_SecondCallIsIdempotentNoOp_NotAnError()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var first = await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(first.IsSuccess);
                Assert.False(first.Value.WasDuplicate);

                var second = await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(second.IsSuccess);
                Assert.True(second.Value.WasDuplicate);
                Assert.Equal(0, second.Value.EventsApplied);

                Assert.Single(highContext.Pages); // not duplicated
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_TamperedPreviousManifestHash_ReturnsBundleChainMismatchError()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundle1 = await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var firstResult = await importService.ImportAsync(bundle1.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(firstResult.IsSuccess);

                // Produce a genuine "bundle 2" via a second real export - its manifest
                // correctly declares BundleNumber=2 and the correct PreviousManifestHash
                // for bundle 1 - then tamper ONLY the PreviousManifestHash field in place
                // (leaving BundleNumber and events.ndjson/PayloadSha256 untouched), so the
                // bundle-number check passes and only the chain check can catch it. An
                // earlier version of this test instead substituted bundle 1's raw bytes
                // under bundle 2's filename, which just re-delivers "bundle number 1"
                // (the manifest's declared number, not the filename, is what's checked) -
                // that hits the idempotent-duplicate path, not a chain mismatch, and is
                // covered separately by Import_SameBundleTwice_SecondCallIsIdempotentNoOp_NotAnError.
                var anotherPage = TestData.NewPage(space, "second-page");
                lowContext.Pages.Add(anotherPage);
                lowContext.RaiseDomainEvent(new PageCreatedEvent(anotherPage.Id, space.Id, space.Key, actor.Id, anotherPage.Title));
                lowContext.AuditContext = AuditCtx;
                lowContext.SaveChanges();
                var bundle2 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(bundle2);
                Assert.Equal(2, bundle2!.BundleNumber);

                using (var archive = System.IO.Compression.ZipFile.Open(bundle2.BundleFilePath, System.IO.Compression.ZipArchiveMode.Update))
                {
                    var manifestEntry = archive.GetEntry("manifest.json")!;
                    System.Text.Json.Nodes.JsonObject manifestJson;
                    using (var readStream = manifestEntry.Open())
                    {
                        manifestJson = System.Text.Json.Nodes.JsonNode.Parse(readStream)!.AsObject();
                    }

                    manifestJson["previousManifestHash"] = new string('f', 64); // a well-formed but wrong hash
                    manifestEntry.Delete();
                    var rewritten = archive.CreateEntry("manifest.json");
                    await using var writer = new StreamWriter(rewritten.Open());
                    await writer.WriteAsync(manifestJson.ToJsonString());
                }

                var result = await importService.ImportAsync(bundle2.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.False(result.IsSuccess);
                Assert.IsType<BundleChainMismatchError>(result.Error);
                Assert.Empty(highContext.Pages.Where(p => p.Id == anotherPage.Id)); // nothing from bundle 2 partially applied
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_CorruptedEventsFile_ReturnsBundlePayloadTamperedError()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundleInfo = await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            // Tamper with the events entry in place, inside the zip, without touching the manifest.
            var eventsEntryName = BundleFormat.EventsEntryName(BundleFormat.CurrentVersion);
            using (var archive = System.IO.Compression.ZipFile.Open(bundleInfo.BundleFilePath, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry(eventsEntryName)!;
                entry.Delete();
                var newEntry = archive.CreateEntry(eventsEntryName);
                await using var writer = new StreamWriter(newEntry.Open());
                await writer.WriteAsync("{\"tampered\":true}");
            }

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundleInfo.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.False(result.IsSuccess);
                Assert.IsType<BundlePayloadTamperedError>(result.Error);
                Assert.Empty(highContext.Pages);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportIncremental_WithNothingPending_ReturnsNull()
    {
        using var lowContext = CreateContext();
        var storage = CreateFileStorage(out var storageDir);
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var result = await exportService.ExportIncrementalAsync(CreateBundleOutputDir(), LowInstanceId);

            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
        }
    }

    [Fact]
    public async Task TwoSequentialIncrementalBundles_ApplyInOrder_AdvancingPerSpaceSequence()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var page1 = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "page-1", "Page 1", "# 1"), EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(page1.IsSuccess);
            var exportService = new BundleExportService(lowContext, storage);
            var bundle1 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundle1);

            var page2 = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "page-2", "Page 2", "# 2"), EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(page2.IsSuccess);
            var bundle2 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundle2);
            Assert.Equal(2, bundle2!.BundleNumber);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result1 = await importService.ImportAsync(bundle1!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result1.IsSuccess);
                var result2 = await importService.ImportAsync(bundle2.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(result2.IsSuccess);

                Assert.True(highContext.Pages.Any(p => p.Id == page1.Value.Id));
                Assert.True(highContext.Pages.Any(p => p.Id == page2.Value.Id));

                var spaceState = highContext.SyncSpaceStates.Single(s => s.OriginInstanceId == LowInstanceId && s.SpaceId == space.Id);
                Assert.Equal(2, spaceState.AppliedSequence);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- Revision history (design.md §12: "full snapshot including revision history") --

    [Fact]
    public async Task Baseline_CarriesFullRevisionHistory_ImportMaterializesItWithShadowAuthors()
    {
        var alice = TestData.NewUser();
        alice.DisplayName = "Alice Author";
        alice.Email = "alice@example.com";
        var bob = TestData.NewUser();
        bob.DisplayName = "Bob Editor";
        bob.Email = "bob@example.com";
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.AddRange(alice, bob);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# v1"), EditorPrincipal(), alice.Id, AuditCtx);
        Assert.True(created.IsSuccess);
        var edited = await pageService.UpdatePageContentAsync(
            new UpdatePageContentRequest(created.Value.Id, 1, "Home v2", "# v2", "tightened wording"),
            EditorPrincipal(), bob.Id, AuditCtx);
        Assert.True(edited.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var baseline = await exportService.ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            // The bundle file declares the format it is: manifest formatVersion 2, events
            // under the version-specific entry name, and NO legacy entry - a format-1
            // importer must hit its missing-events.ndjson guard, never a silent absorb.
            using (var archive = System.IO.Compression.ZipFile.OpenRead(baseline.BundleFilePath))
            {
                Assert.Null(archive.GetEntry("events.ndjson"));
                Assert.NotNull(archive.GetEntry(BundleFormat.EventsEntryName(BundleFormat.CurrentVersion)));
                using var manifestStream = archive.GetEntry("manifest.json")!.Open();
                using var manifestJson = System.Text.Json.JsonDocument.Parse(manifestStream);
                Assert.Equal(BundleFormat.CurrentVersion, manifestJson.RootElement.GetProperty("formatVersion").GetInt32());
            }

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                Assert.True((await importService.ImportAsync(baseline.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var revisions = highContext.PageRevisions
                    .Where(r => r.PageId == created.Value.Id).OrderBy(r => r.RevisionNumber).ToList();
                Assert.Equal(2, revisions.Count);
                Assert.Equal("Home", revisions[0].Title);
                Assert.Equal("# v1", revisions[0].Content);
                Assert.Null(revisions[0].EditSummary);
                Assert.Equal(alice.Id, revisions[0].AuthorUserId);
                Assert.Equal("Home v2", revisions[1].Title);
                Assert.Equal("# v2", revisions[1].Content);
                Assert.Equal("tightened wording", revisions[1].EditSummary);
                Assert.Equal(bob.Id, revisions[1].AuthorUserId);

                // The low-side timestamps survive, so the history timeline stays honest.
                var lowRevisions = lowContext.PageRevisions.AsNoTracking()
                    .Where(r => r.PageId == created.Value.Id).OrderBy(r => r.RevisionNumber).ToList();
                Assert.Equal(lowRevisions[0].CreatedAtUtc, revisions[0].CreatedAtUtc);
                Assert.Equal(lowRevisions[1].CreatedAtUtc, revisions[1].CreatedAtUtc);

                // design.md §12: revision authors arrive as shadow users - flagged
                // external, never loginable - so bylines render on the replica.
                var shadowAlice = highContext.Users.Single(u => u.Id == alice.Id);
                Assert.True(shadowAlice.IsExternal);
                Assert.Null(shadowAlice.Subject);
                Assert.Equal("Alice Author", shadowAlice.DisplayName);
                var shadowBob = highContext.Users.Single(u => u.Id == bob.Id);
                Assert.True(shadowBob.IsExternal);
                Assert.Equal("bob@example.com", shadowBob.Email);

                // Overlap dedupe: the create and edit were ALSO journaled to the outbox
                // before the baseline was cut, so the incremental bundle re-delivers
                // revisions 1 and 2. Immutable history lands once, not twice.
                var incremental = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(incremental);
                Assert.True((await importService.ImportAsync(incremental!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);
                Assert.Equal(2, highContext.PageRevisions.Count(r => r.PageId == created.Value.Id));
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Incremental_EditAfterBaseline_AppendsThatRevisionOnTheReplica_NoHistoryDrift()
    {
        var actor = TestData.NewUser();
        actor.DisplayName = "Drift Author";
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.Add(EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "drift", "Drift", "# v1"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundle1 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId); // drains the create
            Assert.NotNull(bundle1);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                Assert.True((await importService.ImportAsync(bundle1!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);
                Assert.Single(highContext.PageRevisions.Where(r => r.PageId == created.Value.Id).ToList());

                // The edit happens AFTER the first bundle landed. If incremental
                // PageUpserts carried only current content, the replica's history would
                // silently stop at revision 1 while the page said revision 2 - the drift
                // this test exists to rule out.
                var edited = await pageService.UpdatePageContentAsync(
                    new UpdatePageContentRequest(created.Value.Id, 1, "Drift v2", "# v2", "second pass"),
                    EditorPrincipal(), actor.Id, AuditCtx);
                Assert.True(edited.IsSuccess);
                var bundle2 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(bundle2);
                Assert.True((await importService.ImportAsync(bundle2!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var revisions = highContext.PageRevisions
                    .Where(r => r.PageId == created.Value.Id).OrderBy(r => r.RevisionNumber).ToList();
                Assert.Equal(2, revisions.Count);
                Assert.Equal("# v2", revisions[1].Content);
                Assert.Equal("second pass", revisions[1].EditSummary);
                Assert.Equal(actor.Id, revisions[1].AuthorUserId);
                Assert.True(highContext.Users.Single(u => u.Id == actor.Id).IsExternal);

                var page = highContext.Pages.Single(p => p.Id == created.Value.Id);
                Assert.Equal(2, page.CurrentRevisionNumber); // page and history agree
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- Format eras (BundleFormat): accept the past, refuse the future ---------------

    /// <summary>Writes a bundle zip the way a given era's exporter would - manifest as raw JSON, events under the caller's chosen entry name.</summary>
    private static void WriteRawBundle(string path, object manifest, string? eventsEntryName, string? ndjson)
    {
        var webOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        using var fileStream = new FileStream(path, FileMode.CreateNew);
        using var archive = new System.IO.Compression.ZipArchive(fileStream, System.IO.Compression.ZipArchiveMode.Create);

        using (var manifestStream = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
        {
            manifestStream.Write(System.Text.Json.JsonSerializer.Serialize(manifest, webOptions));
        }

        if (eventsEntryName is not null && ndjson is not null)
        {
            using var eventsStream = new StreamWriter(archive.CreateEntry(eventsEntryName).Open());
            eventsStream.Write(ndjson);
        }
    }

    [Fact]
    public async Task Import_LegacyFormat1Bundle_AcceptedAsCurrentStateOnly()
    {
        // A bundle exactly as the format-1 exporter wrote it: no formatVersion field in
        // the manifest, events under "events.ndjson", PageUpsert payload without a
        // revisions array. Already-produced bundles sitting in a transfer directory (or
        // a not-yet-upgraded low side) must keep importing - as the current-state-only
        // snapshot format 1 always was, with no history materialized. Documented, not
        // an error.
        var webOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var pageId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            pageId,
            spaceId,
            parentPageId = (Guid?)null,
            ancestorPath = "/",
            slug = "legacy",
            title = "Legacy Page",
            sortOrder = 0,
            content = "# Legacy",
            revisionNumber = 1,
        }, webOptions);
        var ndjson = System.Text.Json.JsonSerializer.Serialize(
            new NdjsonEventRecord("LEG", spaceId, 1, nameof(SyncEventType.PageUpsert), payload, DateTime.UtcNow), webOptions) + "\n";
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ndjson)));

        var outputDir = CreateBundleOutputDir();
        Directory.CreateDirectory(outputDir);
        var storage = CreateFileStorage(out var storageDir);
        try
        {
            var bundlePath = Path.Combine(outputDir, "bundle-000001.zip");
            WriteRawBundle(bundlePath, new
            {
                instanceId = LowInstanceId,
                bundleNumber = 1,
                previousManifestHash = (string?)null,
                payloadSha256 = payloadHash,
                spaceEventRanges = new Dictionary<string, object>
                {
                    ["LEG"] = new { spaceId, fromSequence = 1, toSequence = 1, eventCount = 1 },
                },
            }, "events.ndjson", ndjson);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundlePath, LowInstanceId, AuditCtx);

                Assert.True(result.IsSuccess);
                Assert.Equal(1, result.Value.EventsApplied);
                var page = highContext.Pages.Single(p => p.Id == pageId);
                Assert.Equal("Legacy Page", page.Title);
                Assert.Equal("# Legacy", page.CurrentContent);
                Assert.Empty(highContext.PageRevisions.ToList()); // format 1 never carried history
                Assert.Equal(1, highContext.SyncImportStates.Single().LastBundleNumber);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_FutureFormatVersion_RefusedLoudly_NothingLands()
    {
        // The other direction of the era contract: a manifest declaring a format NEWER
        // than this instance understands is refused before a single event is parsed -
        // never partially understood, same philosophy as the hash chain.
        var outputDir = CreateBundleOutputDir();
        Directory.CreateDirectory(outputDir);
        var storage = CreateFileStorage(out var storageDir);
        try
        {
            var bundlePath = Path.Combine(outputDir, "bundle-000001.zip");
            WriteRawBundle(bundlePath, new
            {
                instanceId = LowInstanceId,
                bundleNumber = 1,
                previousManifestHash = (string?)null,
                payloadSha256 = new string('0', 64),
                spaceEventRanges = new Dictionary<string, object>(),
                formatVersion = 99,
            }, "events.v99.ndjson", "{\"whatever\":\"a future era's shape\"}\n");

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundlePath, LowInstanceId, AuditCtx);

                Assert.False(result.IsSuccess);
                var error = Assert.IsType<BundleFormatUnsupportedError>(result.Error);
                Assert.Equal(99, error.BundleFormatVersion);
                Assert.Equal(BundleFormat.CurrentVersion, error.MaxSupportedVersion);
                Assert.Empty(highContext.Pages.IgnoreQueryFilters().ToList());
                Assert.Empty(highContext.SyncImportStates.ToList()); // position never advanced
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- Field completeness (design.md §12) -------------------------------------------
    //
    // A hand-listed payload builder plus a round-trip test that checks three fields is how
    // a field gets silently dropped, and this file records that class of bug shipping
    // TWICE — the baseline that was not a full snapshot, and the page icons. Both were
    // fixed by adding another named per-field assertion, which is a reaction, not a guard.
    // Below, the property list comes from reflection, so adding a column to Page breaks
    // this test until someone decides whether it crosses; the decision, not the
    // discovery, is what is written down by hand.

    /// <summary>
    /// Every scalar property of <see cref="Page"/>, mapped to the wire key it is exported
    /// as, or to null with the reason it is not. Navigation properties are excluded by the
    /// sweep itself — they are not fields, they are other tables.
    /// </summary>
    private static readonly Dictionary<string, string?> PageFieldExportContract = new(StringComparer.Ordinal)
    {
        ["Id"] = "pageId",
        ["SpaceId"] = "spaceId",
        ["ParentPageId"] = "parentPageId",
        ["AncestorPath"] = "ancestorPath",
        ["Slug"] = "slug",
        ["Title"] = "title",
        ["Icon"] = "icon",
        ["SortOrder"] = "sortOrder",
        ["CurrentRevisionNumber"] = "revisionNumber",
        ["CurrentContent"] = "content",

        // Deliberately not exported. Each is instance-local state about THIS side's copy,
        // not a property of the content (design.md §12).
        ["IsDeleted"] = null,        // deletion crosses as its own PageDelete event, never as a flag on an upsert
        ["DeletedAtUtc"] = null,     // trash bookkeeping is local (data-model.md's 30-day window)
        ["DeletedByUserId"] = null,  // naming the local user who trashed it would send an identity the other side has no row for
        ["DeleteBatchId"] = null,    // groups a local subtree delete for local undo; meaningless elsewhere
        ["CreatedAtUtc"] = null,     // the receiving row's own timestamps; revisions carry the authored times that matter
        ["UpdatedAtUtc"] = null,
    };

    [Fact]
    public void EveryPageField_IsEitherExported_OrExplicitlyDeclaredLocal()
    {
        var scalars = typeof(Page).GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => IsScalarField(p.PropertyType))
            .Select(p => p.Name)
            .ToList();

        // Non-vacuous: a reflection query matching nothing would pass everything below.
        Assert.True(scalars.Count > 10, $"Expected Page to expose its scalar columns; found {scalars.Count}.");
        Assert.Contains("SortOrder", scalars);

        var undecided = scalars.Where(name => !PageFieldExportContract.ContainsKey(name)).ToList();
        Assert.True(undecided.Count == 0,
            "These Page fields are neither exported nor declared instance-local, so a sync bundle would drop them " +
            "in silence (design.md §12). Add the wire key to PageFieldExportContract, or map it to null with the " +
            "reason it stays local:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", undecided));

        var stale = PageFieldExportContract.Keys.Where(name => !scalars.Contains(name)).ToList();
        Assert.True(stale.Count == 0,
            "These entries name Page fields that no longer exist; the contract has outlived what it describes:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", stale));
    }

    [Fact]
    public async Task EveryFieldTheContractCallsExported_IsActuallyInTheBundle()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space, "home");

        // Non-default values, so a field that is exported but always written as its
        // default cannot pass by coincidence — which is exactly how sortOrder went
        // unproven: every payload in this file hand-wrote it as 0, and 0 is also the
        // property's default.
        page.SortOrder = 4321;
        page.Icon = PageIcon.Rocket;
        page.Title = "Home";
        page.CurrentContent = "# Welcome";

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.Pages.Add(page);
        lowContext.SaveChanges();

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var bundle = await new BundleExportService(lowContext, storage)
                .ExportBaselineAsync(space.Id, outputDir, LowInstanceId);

            var upsert = ReadFirstPageUpsertPayload(bundle.BundleFilePath);

            var missing = PageFieldExportContract
                .Where(entry => entry.Value is not null)
                .Where(entry => !upsert.TryGetProperty(entry.Value!, out _))
                .Select(entry => $"{entry.Key} (wire key '{entry.Value}')")
                .ToList();

            Assert.True(missing.Count == 0,
                "The export contract says these fields cross, and the bundle does not contain them:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));

            // The two the round-trip never proved: both are exported AND strictly required
            // on import (BundleImportService throws if either is absent), yet nothing
            // asserted either actually survives.
            Assert.Equal(4321, upsert.GetProperty("sortOrder").GetInt32());
            Assert.Equal(page.AncestorPath, upsert.GetProperty("ancestorPath").GetString());

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                Assert.True((await new BundleImportService(highContext, storage)
                    .ImportAsync(bundle.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var imported = highContext.Pages.Single(p => p.Id == page.Id);
                Assert.Equal(4321, imported.SortOrder);
                Assert.Equal(page.AncestorPath, imported.AncestorPath);
                Assert.Equal(PageIcon.Rocket, imported.Icon);
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>A field, as opposed to a navigation property to another table.</summary>
    private static bool IsScalarField(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTime)
            || underlying == typeof(decimal);
    }

    private static System.Text.Json.JsonElement ReadFirstPageUpsertPayload(string bundlePath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(bundlePath);
        using var stream = archive.GetEntry(BundleFormat.EventsEntryName(BundleFormat.CurrentVersion))!.Open();
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var record = System.Text.Json.JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("eventType").GetString() != nameof(SyncEventType.PageUpsert))
            {
                continue;
            }

            // The line carries the payload as a STRING of JSON, not a nested object.
            using var payload = System.Text.Json.JsonDocument.Parse(
                record.RootElement.GetProperty("payloadJson").GetString()!);
            return payload.RootElement.Clone(); // cloned: both documents die with this scope
        }

        throw new InvalidOperationException("The bundle contained no PageUpsert line.");
    }
}
