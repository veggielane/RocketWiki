using System.IO.Compression;
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
/// design.md §8: "watching a replica is exactly how a user hears that a sync bundle
/// changed it." Two surfaces under test, split by where the work happens:
///
/// 1. <b>Producer</b> (BundleImportService, runs in the offline Sync CLI): an applied
///    bundle writes ONE Notification row of type SyncImported per watcher per bundle -
///    never per event - in the same transaction as the bundle's content, with
///    TitleSnapshot null because no recipient has a live token there to evaluate
///    canView against (§6.1: the local User mirror is never a substitute). Nothing on
///    duplicates, nothing on refusals.
///
/// 2. <b>Read-time gate</b> (NotificationReadModelService): the canView those rows
///    never got at send time happens at fetch, against the caller's live token-built
///    Principal - and it gates the row's EXISTENCE, not just its title, because unlike
///    dispatcher-written rows the recipient never legitimately learned of the event.
///    Fail closed.
/// </summary>
public class SyncBundleNotificationTests : SqliteTestBase
{
    private const string LowInstanceId = "low-instance";
    private const string HighInstanceId = "high-instance";

    // This class's primary context plays the low side: its exported spaces carry
    // OriginInstanceId = LowInstanceId, and the outbox writer's ownership invariant
    // (UseLocalInstanceId, design.md §12) journals nothing unless the context's own
    // instance id matches — same override BundleExportImportTests makes.
    protected override string DefaultLocalInstanceId => LowInstanceId;
    private static readonly AuditContext AuditCtx = new(AuditChannel.Sync, "sync-job-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("editor-sub", groups);

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
        tempDir = Path.Combine(Path.GetTempPath(), "rocketwiki-sync-notif-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new FileStorageOptions { FileSystem = new FileSystemFileStorageOptions { Root = tempDir } });
        return new FileSystemFileStorage(options);
    }

    private static string CreateBundleOutputDir() =>
        Path.Combine(Path.GetTempPath(), "rocketwiki-bundles-notif-tests", Guid.NewGuid().ToString("N"));

    private static User NewHighUser(string name)
    {
        var user = TestData.NewUser();
        user.DisplayName = name;
        return user;
    }

    // --- Producer: BundleImportService ------------------------------------------------

    [Fact]
    public async Task Import_WritesOnePageScopedRow_ForPageWatcher_AndNothingForUnaffectedWatcher()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var watched = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "watched", "Watched Replica Page", "# v1"), EditorPrincipal(), actor.Id, AuditCtx);
        var other = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "other", "Other Page", "# other"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(watched.IsSuccess && other.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundle1 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
            Assert.NotNull(bundle1);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);

                // Bundle 1 lands with no watchers on high yet - no rows.
                Assert.True((await importService.ImportAsync(bundle1!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);
                Assert.Empty(highContext.Notifications.AsNoTracking().ToList());

                // Now a high-side user watches the replica page; another watches only
                // the page bundle 2 will NOT touch.
                var pageWatcher = NewHighUser("Page Watcher");
                var unaffectedWatcher = NewHighUser("Unaffected Watcher");
                highContext.Users.AddRange(pageWatcher, unaffectedWatcher);
                highContext.Watches.Add(new Watch { UserId = pageWatcher.Id, PageId = watched.Value.Id, CreatedAtUtc = DateTime.UtcNow });
                highContext.Watches.Add(new Watch { UserId = unaffectedWatcher.Id, PageId = other.Value.Id, CreatedAtUtc = DateTime.UtcNow });
                highContext.SaveChanges();

                // Bundle 2: one edit to the watched page only.
                var edit = await pageService.UpdatePageContentAsync(
                    new UpdatePageContentRequest(watched.Value.Id, 1, "Watched Replica Page", "# v2", null),
                    EditorPrincipal(), actor.Id, AuditCtx);
                Assert.True(edit.IsSuccess);
                var bundle2 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);
                Assert.NotNull(bundle2);

                Assert.True((await importService.ImportAsync(bundle2!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var row = Assert.Single(highContext.Notifications.AsNoTracking().ToList());
                Assert.Equal(pageWatcher.Id, row.RecipientUserId); // the unaffected watcher hears nothing
                Assert.Equal(NotificationType.SyncImported, row.Type);
                Assert.Equal(watched.Value.Id, row.PageId); // exactly one affected watched page -> page-scoped
                Assert.Equal(space.Id, row.SpaceId);
                Assert.Null(row.ActorUserId); // system action - renders as "System"
                Assert.Null(row.TitleSnapshot); // no canView was evaluated offline; nothing attested, nothing disclosed
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Import_MultiEventBundle_WritesOneRowPerWatcher_NotOnePerEvent_AndDuplicateImportWritesNothing()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var seed = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "seed", "Seed", "# seed"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(seed.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundle1 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                var importService = new BundleImportService(highContext, storage);
                Assert.True((await importService.ImportAsync(bundle1!.BundleFilePath, LowInstanceId, AuditCtx)).IsSuccess);

                var spaceWatcher = NewHighUser("Space Watcher");
                highContext.Users.Add(spaceWatcher);
                highContext.Watches.Add(new Watch { UserId = spaceWatcher.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });
                highContext.SaveChanges();

                // Bundle 2 carries THREE events (two creates + one edit)...
                var a = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "a", "A", "# a"), EditorPrincipal(), actor.Id, AuditCtx);
                var b = await pageService.CreatePageAsync(new CreatePageRequest(space.Id, null, "b", "B", "# b"), EditorPrincipal(), actor.Id, AuditCtx);
                Assert.True(a.IsSuccess && b.IsSuccess);
                var edit = await pageService.UpdatePageContentAsync(
                    new UpdatePageContentRequest(seed.Value.Id, 1, "Seed", "# seed v2", null), EditorPrincipal(), actor.Id, AuditCtx);
                Assert.True(edit.IsSuccess);
                var bundle2 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);

                var applied = await importService.ImportAsync(bundle2!.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(applied.IsSuccess);
                Assert.Equal(3, applied.Value.EventsApplied);

                // ...but the watcher gets ONE row, space-scoped (several pages affected).
                var row = Assert.Single(highContext.Notifications.AsNoTracking().ToList());
                Assert.Equal(spaceWatcher.Id, row.RecipientUserId);
                Assert.Equal(NotificationType.SyncImported, row.Type);
                Assert.Null(row.PageId);
                Assert.Equal(space.Id, row.SpaceId);

                // Idempotent duplicate delivery (design.md §12) must not re-notify.
                var duplicate = await importService.ImportAsync(bundle2.BundleFilePath, LowInstanceId, AuditCtx);
                Assert.True(duplicate.IsSuccess);
                Assert.True(duplicate.Value.WasDuplicate);
                Assert.Single(highContext.Notifications.AsNoTracking().ToList());
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RefusedImport_WritesNoNotificationRows()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var lowContext = CreateContext();
        lowContext.Users.Add(actor);
        lowContext.Spaces.Add(space);
        lowContext.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        lowContext.SaveChanges();

        var pageService = new PageService(lowContext, LowInstanceId);
        var page = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "p", "P", "# p"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page.IsSuccess);

        var storage = CreateFileStorage(out var storageDir);
        var outputDir = CreateBundleOutputDir();
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundle1 = await exportService.ExportIncrementalAsync(outputDir, LowInstanceId);

            // Tamper with the payload so the SHA-256 check refuses the bundle.
            var eventsEntryName = BundleFormat.EventsEntryName(BundleFormat.CurrentVersion);
            using (var archive = ZipFile.Open(bundle1!.BundleFilePath, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry(eventsEntryName)!;
                string events;
                using (var reader = new StreamReader(entry.Open()))
                {
                    events = reader.ReadToEnd();
                }

                entry.Delete();
                var rewritten = archive.CreateEntry(eventsEntryName);
                using var writer = new StreamWriter(rewritten.Open());
                writer.Write(events + "\n");
            }

            var (highConnection, highContext) = CreateSecondaryDatabase();
            using (highConnection)
            using (highContext)
            {
                // A space watcher exists (the replica space row seeded directly, as if
                // from an earlier baseline) - so if refusal DID fan out, there would be
                // a candidate to catch.
                var replicaSpace = NewExportedSpace();
                replicaSpace.Id = space.Id;
                replicaSpace.IsExported = false;
                var watcher = NewHighUser("Watcher");
                highContext.Spaces.Add(replicaSpace);
                highContext.Users.Add(watcher);
                highContext.Watches.Add(new Watch { UserId = watcher.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });
                highContext.SaveChanges();

                var importService = new BundleImportService(highContext, storage);
                var result = await importService.ImportAsync(bundle1.BundleFilePath, LowInstanceId, AuditCtx);

                Assert.False(result.IsSuccess);
                Assert.IsType<BundlePayloadTamperedError>(result.Error);
                Assert.Empty(highContext.Notifications.AsNoTracking().ToList()); // rows ride the import transaction
            }
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- Read-time gate: NotificationReadModelService ---------------------------------

    private (Guid RecipientId, Guid PageId, Guid SpaceId) SeedReplicaRowsForReadModel(RocketWikiDbContext db)
    {
        var recipient = NewHighUser("Recipient");
        var space = new Space
        {
            Key = "REP",
            Name = "Replica Space",
            OriginInstanceId = LowInstanceId, // != HighInstanceId -> replica
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
        };
        var page = TestData.NewPage(space, "landed");
        page.Title = "Live Replica Title";

        db.Users.Add(recipient);
        db.Spaces.Add(space);
        db.Pages.Add(page);
        // Viewer access requires the "eng" group - the pivot both assertions turn on.
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = """{ "group": "eng" }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });
        db.Notifications.Add(new Notification
        {
            RecipientUserId = recipient.Id,
            Type = NotificationType.SyncImported,
            PageId = page.Id,
            SpaceId = space.Id,
            TitleSnapshot = null, // what BundleImportService writes
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.Notifications.Add(new Notification
        {
            RecipientUserId = recipient.Id,
            Type = NotificationType.SyncImported,
            PageId = null, // space-scoped
            SpaceId = space.Id,
            TitleSnapshot = null,
            CreatedAtUtc = DateTime.UtcNow,
        });
        // Control row: dispatcher-written type, canView'd at send time - must keep the
        // established semantics (row survives, only the title is gated).
        db.Notifications.Add(new Notification
        {
            RecipientUserId = recipient.Id,
            Type = NotificationType.PageUpdated,
            PageId = page.Id,
            SpaceId = space.Id,
            TitleSnapshot = "Title As Permitted At Send Time",
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
        return (recipient.Id, page.Id, space.Id);
    }

    [Fact]
    public async Task SyncImportedRows_SurfaceWithLiveTitle_ForCallerWhoPassesCanViewNow()
    {
        using var db = CreateContext();
        var (recipientId, pageId, spaceId) = SeedReplicaRowsForReadModel(db);

        var service = new NotificationReadModelService(db, HighInstanceId);
        var items = await service.GetNotificationsAsync(recipientId, EditorPrincipal("eng"), take: 10);

        Assert.Equal(3, items.Count);

        var pageScoped = Assert.Single(items, i => i.Type == NotificationType.SyncImported && i.PageId == pageId);
        // The row carries no snapshot; the caller passes canView NOW, so the LIVE title
        // is served - they could open the page and read it anyway.
        Assert.Equal("Live Replica Title", pageScoped.PageTitle);
        Assert.Equal("REP", pageScoped.SpaceKey);
        Assert.Equal("System", pageScoped.ActorDisplayName);

        var spaceScoped = Assert.Single(items, i => i.Type == NotificationType.SyncImported && i.PageId is null);
        Assert.Null(spaceScoped.PageTitle);
        Assert.Equal("REP", spaceScoped.SpaceKey);
    }

    [Fact]
    public async Task MarkRead_OnSyncImportedRow_SucceedsForCallerWithAccess_ButWithholdsPageIdAndSpaceKey()
    {
        using var db = CreateContext();
        var (recipientId, _, _) = SeedReplicaRowsForReadModel(db);
        var rowId = db.Notifications.AsNoTracking()
            .First(n => n.RecipientUserId == recipientId && n.Type == NotificationType.SyncImported && n.PageId != null).Id;

        var service = new NotificationReadModelService(db, HighInstanceId);
        var marked = await service.MarkNotificationReadAsync(rowId, recipientId, EditorPrincipal("eng"),
            new AuditContext(AuditChannel.GraphQl, "req-mark", "127.0.0.1"));

        // The caller's live Principal passes the existence gate, so the mark succeeds -
        // but the receipt still withholds the subject: mark-read spends its canView on
        // the row's EXISTENCE only, and the list is the one disclosure surface.
        Assert.True(marked.IsSuccess);
        Assert.NotNull(marked.Value.ReadAtUtc);
        Assert.Null(marked.Value.PageId);
        Assert.Null(marked.Value.SpaceKey);
        Assert.Null(marked.Value.PageTitle);
    }

    [Fact]
    public async Task MarkRead_OnExistenceGatedRow_IsNotFoundForCallerWithoutAccess_AndLeavesRowUnread()
    {
        using var db = CreateContext();
        var (recipientId, _, _) = SeedReplicaRowsForReadModel(db);
        var rowId = db.Notifications.AsNoTracking()
            .First(n => n.RecipientUserId == recipientId && n.Type == NotificationType.SyncImported && n.PageId != null).Id;

        var service = new NotificationReadModelService(db, HighInstanceId);
        var marked = await service.MarkNotificationReadAsync(rowId, recipientId, EditorPrincipal(/* not in "eng" */),
            new AuditContext(AuditChannel.GraphQl, "req-mark-probe", "127.0.0.1"));

        // The list suppresses this row's existence for a caller who fails the gate, and
        // mark-read must not become the probe around that: even the bare receipt (type +
        // timestamp) would confirm the row exists. NotFound, indistinguishable from a
        // nonexistent id - and the row stays unread, so it surfaces intact if access is
        // ever restored.
        Assert.False(marked.IsSuccess);
        Assert.IsType<NotFoundError>(marked.Error);
        Assert.Null(db.Notifications.AsNoTracking().Single(n => n.Id == rowId).ReadAtUtc);
    }

    [Fact]
    public async Task SyncImportedRows_AreFullySuppressed_ForCallerWithoutAccessNow()
    {
        using var db = CreateContext();
        var (recipientId, pageId, _) = SeedReplicaRowsForReadModel(db);

        var service = new NotificationReadModelService(db, HighInstanceId);
        var items = await service.GetNotificationsAsync(recipientId, EditorPrincipal(/* not in "eng" */), take: 10);

        // Both SyncImported rows vanish ENTIRELY - no canView ever held for them, so
        // even their existence would leak that a restricted replica page/space is
        // active. The dispatcher-written row keeps the established shape: it survives
        // (the recipient legitimately learned of the event at send time) minus title.
        var survivor = Assert.Single(items);
        Assert.Equal(NotificationType.PageUpdated, survivor.Type);
        Assert.Equal(pageId, survivor.PageId);
        Assert.Null(survivor.PageTitle);
    }
}
