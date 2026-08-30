using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Storage;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §12, at the API tier (milestone 6):
/// - Replica read-only enforcement as a CALLER sees it: GraphQL mutations and the
///   attachment POST refuse with the typed ReadOnlyReplica error carrying
///   originInstanceId (what the web's ReadOnlyReplicaDialog renders), and the denial
///   is audited (design.md §7: denials on every channel). The per-service refusals
///   themselves are covered service-by-service in RocketWiki.Data.Tests.
/// - The admin syncStatus query: per-origin last bundle / chain hash / per-space
///   positions after a REAL bundle import, low-side outbox positions, admin-gated
///   with the denial audited (same shape as auditEvents).
/// The API host runs with the default Instance:Id "standalone", so a space seeded
/// with any other OriginInstanceId is a replica here.
/// </summary>
public sealed class ReplicaAndSyncStatusTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string LowOrigin = "low-instance";

    private async Task<(Guid SpaceId, Guid PageId)> SeedReplicaSpaceWithPageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        // A replica as BundleImportService materializes one: origin is the low
        // instance, and the local (high-side) admin granted everyone Editor - which
        // must still not matter, the invariant sits beneath every grant (§6.4).
        var space = new Space
        {
            Key = $"RP{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Replica Space",
            OriginInstanceId = LowOrigin,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "mirrored", Title = "Mirrored Page", CurrentRevisionNumber = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        return (space.Id, page.Id);
    }

    private HttpClient AuthedClient(IEnumerable<string>? roles = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"user-{Guid.NewGuid()}", roles: roles);
        return client;
    }

    // --- Replica read-only over GraphQL --------------------------------------------------

    [Fact]
    public async Task UpdatePageContent_OnReplica_ReturnsReadOnlyReplicaWithOrigin_AndAuditsDenial()
    {
        var (_, pageId) = await SeedReplicaSpaceWithPageAsync();
        var client = AuthedClient();

        var result = await client.PostGraphQLAsync($$"""
            mutation { updatePageContent(input: { pageId: "{{pageId}}", expectedRevisionNumber: 1, title: "Hax", content: "# Hax", editSummary: null }) {
                page { id } error { kind spaceId originInstanceId } } }
            """);

        var error = result.RootElement.GetProperty("data").GetProperty("updatePageContent").GetProperty("error");
        Assert.Equal("ReadOnlyReplica", error.GetProperty("kind").GetString());
        Assert.Equal(LowOrigin, error.GetProperty("originInstanceId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var denial = await db.AuditEvents
            .Where(e => e.Action == "page.edit" && e.Outcome == AuditOutcome.Denied && e.SubjectId == pageId)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(denial);
        Assert.Contains(LowOrigin, denial.DetailsJson ?? string.Empty);
    }

    [Fact]
    public async Task CommentLabelAndPageMutations_OnReplica_AllRefuseWithReadOnlyReplica()
    {
        var (spaceId, pageId) = await SeedReplicaSpaceWithPageAsync();
        var client = AuthedClient();

        // One mutation per remaining GraphQL category (page-structure, comment, label) -
        // the full per-operation matrix lives in the service-tier tests.
        var mutations = new[]
        {
            ($$"""mutation { deletePage(input: { pageId: "{{pageId}}" }) { error { kind originInstanceId } } }""", "deletePage"),
            ($$"""mutation { addComment(input: { pageId: "{{pageId}}", body: "Hi" }) { error { kind originInstanceId } } }""", "addComment"),
            ($$"""mutation { createLabel(input: { spaceId: "{{spaceId}}", name: "nope" }) { error { kind originInstanceId } } }""", "createLabel"),
        };

        foreach (var (mutation, field) in mutations)
        {
            var result = await client.PostGraphQLAsync(mutation);
            var error = result.RootElement.GetProperty("data").GetProperty(field).GetProperty("error");
            Assert.Equal("ReadOnlyReplica", error.GetProperty("kind").GetString());
            Assert.Equal(LowOrigin, error.GetProperty("originInstanceId").GetString());
        }
    }

    // --- Replica read-only over the attachment POST route --------------------------------

    [Fact]
    public async Task AttachmentUpload_OnReplica_Returns403WithReadOnlyReplicaBody_AndAuditsDenial()
    {
        var (_, pageId) = await SeedReplicaSpaceWithPageAsync();
        var client = AuthedClient();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "x.bin");
        var response = await client.PostAsync($"/attachments/{pageId}", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ReadOnlyReplica", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal(LowOrigin, body.RootElement.GetProperty("originInstanceId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var denialExists = await db.AuditEvents
            .AnyAsync(e => e.Action == "attachment.upload" && e.Outcome == AuditOutcome.Denied
                && e.DetailsJson != null && e.DetailsJson.Contains(LowOrigin));
        Assert.True(denialExists, "The replica-refused upload must be audited as a denial on the attachment channel.");
    }

    // --- syncStatus ----------------------------------------------------------------------

    [Fact]
    public async Task SyncStatus_AsNonAdmin_ReturnsGraphQLError_AndRecordsDenial()
    {
        var client = AuthedClient();

        var result = await client.PostGraphQLAsync("{ syncStatus { localInstanceId } }");

        Assert.True(result.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.GetArrayLength() > 0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var denied = await db.AuditEvents.AnyAsync(e => e.Action == "sync.status" && e.Outcome == AuditOutcome.Denied);
        Assert.True(denied, "A non-admin's syncStatus attempt must itself be recorded as a denial.");
    }

    [Fact]
    public async Task SyncStatus_AfterRealBundleImport_ReflectsOriginPositionAndChainState()
    {
        // Produce a genuine bundle on a scratch "low" database, exactly as the
        // RocketWiki.Sync CLI would, then import it into THIS API host's database.
        var bundleDir = Path.Combine(Path.GetTempPath(), "rocketwiki-syncstatus-tests", Guid.NewGuid().ToString("N"));
        using var lowConnection = new SqliteConnection("DataSource=:memory:");
        lowConnection.Open();
        try
        {
            // This scratch database IS the low instance, so its context declares
            // LowOrigin — the outbox writer only journals exported spaces whose
            // OriginInstanceId matches the context's own id (design.md §12).
            var lowOptions = new DbContextOptionsBuilder<RocketWikiDbContext>()
                .UseSqlite(lowConnection).UseLocalInstanceId(LowOrigin).Options;
            string spaceKey;
            Guid spaceId;
            using (var low = new RocketWikiDbContext(lowOptions))
            {
                low.Database.EnsureCreated();
                var author = new User { Subject = "low-author", DisplayName = "Low Author", AttributesJson = "{}", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
                var space = new Space { Key = $"SY{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Synced", OriginInstanceId = LowOrigin, IsExported = true, CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = Guid.NewGuid() };
                spaceKey = space.Key;
                spaceId = space.Id;
                low.Users.Add(author);
                low.Spaces.Add(space);
                low.AccessRules.Add(new AccessRule
                {
                    Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
                    ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                    CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = author.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = author.Id,
                });
                low.SaveChanges();

                // Through the real service so the outbox journals it (sequence 1).
                var pageService = new PageService(low, LowOrigin);
                var created = await pageService.CreatePageAsync(
                    new CreatePageRequest(space.Id, null, "synced", "Synced Page", "# Synced"),
                    Principal.Create("low-editor", []), author.Id,
                    new AuditContext(AuditChannel.GraphQl, "low-req", "127.0.0.1"));
                Assert.True(created.IsSuccess);

                var storage = new FileSystemFileStorage(Microsoft.Extensions.Options.Options.Create(new FileStorageOptions
                {
                    FileSystem = new FileSystemFileStorageOptions { Root = Path.Combine(bundleDir, "low-storage") },
                }));
                var exported = await new BundleExportService(low, storage).ExportIncrementalAsync(bundleDir, LowOrigin);
                Assert.NotNull(exported);
            }

            // Import into the API host's own database - the "high" side under test.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
                var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
                var importService = new BundleImportService(db, storage);
                var imported = await importService.ImportAsync(
                    Path.Combine(bundleDir, "bundle-000001.zip"), LowOrigin,
                    new AuditContext(AuditChannel.Sync, "test-import", "cli"));
                Assert.True(imported.IsSuccess);
            }

            var adminClient = AuthedClient(roles: ["admin"]);
            var result = await adminClient.PostGraphQLAsync("""
                { syncStatus {
                    localInstanceId
                    origins { originInstanceId lastBundleNumber lastManifestHash lastImportAtUtc spaces { spaceId spaceKey appliedSequence } } } }
                """);

            var status = result.RootElement.GetProperty("data").GetProperty("syncStatus");
            Assert.Equal("standalone", status.GetProperty("localInstanceId").GetString());

            var origin = status.GetProperty("origins").EnumerateArray()
                .Single(o => o.GetProperty("originInstanceId").GetString() == LowOrigin);
            Assert.Equal(1, origin.GetProperty("lastBundleNumber").GetInt32());
            Assert.Equal(64, origin.GetProperty("lastManifestHash").GetString()!.Length); // a real SHA-256, the link the next bundle must chain from

            var spaceStatus = origin.GetProperty("spaces").EnumerateArray()
                .Single(s => s.GetProperty("spaceId").GetString() == spaceId.ToString());
            Assert.Equal(spaceKey, spaceStatus.GetProperty("spaceKey").GetString());
            Assert.Equal(1, spaceStatus.GetProperty("appliedSequence").GetInt64());
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncStatus_ShowsExportedSpaces_WithPendingOutboxCounts()
    {
        Guid spaceId;
        string spaceKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            // A native exported space on THIS instance (Instance:Id "standalone") with
            // one drained and two pending outbox events.
            var space = new Space { Key = $"EX{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Exported", OriginInstanceId = "standalone", IsExported = true, LastOutboxSequence = 3, CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = Guid.NewGuid() };
            spaceId = space.Id;
            spaceKey = space.Key;
            db.Spaces.Add(space);
            db.SyncOutboxEvents.AddRange(
                new SyncOutboxEvent { SpaceId = space.Id, SequenceNumber = 1, EventType = SyncEventType.PageUpsert, PayloadJson = "{}", CreatedAtUtc = DateTime.UtcNow, ExportedInBundle = 1 },
                new SyncOutboxEvent { SpaceId = space.Id, SequenceNumber = 2, EventType = SyncEventType.PageUpsert, PayloadJson = "{}", CreatedAtUtc = DateTime.UtcNow },
                new SyncOutboxEvent { SpaceId = space.Id, SequenceNumber = 3, EventType = SyncEventType.Comment, PayloadJson = "{}", CreatedAtUtc = DateTime.UtcNow });

            // A replica flagged exported must NOT appear: only a native space can be
            // exported (design.md §12) - the status page must not present a replica as
            // an export source even if the flag is somehow set.
            db.Spaces.Add(new Space { Key = $"BX{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Bogus", OriginInstanceId = LowOrigin, IsExported = true, CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        var adminClient = AuthedClient(roles: ["admin"]);
        var result = await adminClient.PostGraphQLAsync("""
            { syncStatus { exportedSpaces { spaceId spaceKey lastOutboxSequence pendingEventCount lastExportedBundle } } }
            """);

        var exportedSpaces = result.RootElement.GetProperty("data").GetProperty("syncStatus").GetProperty("exportedSpaces");
        var mine = exportedSpaces.EnumerateArray().Single(s => s.GetProperty("spaceId").GetString() == spaceId.ToString());
        Assert.Equal(spaceKey, mine.GetProperty("spaceKey").GetString());
        Assert.Equal(3, mine.GetProperty("lastOutboxSequence").GetInt64());
        Assert.Equal(2, mine.GetProperty("pendingEventCount").GetInt32());
        Assert.Equal(1, mine.GetProperty("lastExportedBundle").GetInt32());

        Assert.DoesNotContain(exportedSpaces.EnumerateArray(),
            s => s.GetProperty("spaceKey").GetString()!.StartsWith("BX", StringComparison.Ordinal));
    }
}
