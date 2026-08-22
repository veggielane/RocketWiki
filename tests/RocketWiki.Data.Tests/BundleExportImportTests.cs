using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
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
/// Known simplification, flagged: baseline export snapshots each page's CURRENT state
/// only, not its full PageRevision history (design.md §12 literally asks for "including
/// revision history"). PageRevision-author shadow-user mapping also isn't implemented -
/// only CommentAddedEvent's author path is, which is enough to prove the mechanism.
/// </summary>
public class BundleExportImportTests : SqliteTestBase
{
    private const string LowInstanceId = "low-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.Sync, "sync-job-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("editor-sub", groups);

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

    // --- Gap-refusing, idempotent, chain-verified -------------------------------------

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

            // Tamper with events.ndjson in place, inside the zip, without touching the manifest.
            using (var archive = System.IO.Compression.ZipFile.Open(bundleInfo.BundleFilePath, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("events.ndjson")!;
                entry.Delete();
                var newEntry = archive.CreateEntry("events.ndjson");
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
}
