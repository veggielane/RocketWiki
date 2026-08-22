using System.Security.Cryptography;
using System.Text;
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
/// design.md §10: write bytes to storage first, then commit the Attachment row + audit
/// event in one transaction; downloads are permission-checked before streaming, and an
/// attachment on a page the caller can't view is absent, not forbidden. Uses the real
/// FileSystemFileStorage against a temp directory (design.md §14's own prescribed
/// testing approach), not a hand-rolled fake.
/// </summary>
public class AttachmentServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("editor-sub", groups);
    private static Principal ViewerOnlyPrincipal() => Principal.Create("viewer-sub", Array.Empty<string>());

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

    private static AccessRule ViewerGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Viewer,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule ViewRestriction(Guid pageId, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = expressionJson,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static IFileStorage CreateFileStorage(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "rocketwiki-attachment-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new FileStorageOptions { FileSystem = new FileSystemFileStorageOptions { Root = tempDir } });
        return new FileSystemFileStorage(options);
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    // --- Upload -------------------------------------------------------------------

    [Fact]
    public async Task Upload_Succeeds_WritesBytesToStorage_ComputesContentHash()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var service = new AttachmentService(context, storage, LocalInstanceId);
            var fileBytes = "diagram contents"u8.ToArray();
            var expectedHash = SHA256.HashData(fileBytes);

            var result = await service.UploadAsync(
                new UploadAttachmentRequest(page.Id, "diagram.png", "image/png", new MemoryStream(fileBytes)),
                EditorPrincipal(), actor.Id, AuditCtx);

            Assert.True(result.IsSuccess);
            Assert.Equal(expectedHash, result.Value.ContentHash);
            Assert.Equal(fileBytes.Length, result.Value.SizeBytes);
            Assert.True(await storage.ExistsAsync(result.Value.StorageKey, CancellationToken.None));
            Assert.Contains(context.AuditEvents, e => e.Action == "attachment.upload");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Upload_ByViewerOnly_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(ViewerGrant(space.Id));
            context.SaveChanges();

            var service = new AttachmentService(context, storage, LocalInstanceId);
            var result = await service.UploadAsync(
                new UploadAttachmentRequest(page.Id, "file.txt", "text/plain", Content("data")),
                ViewerOnlyPrincipal(), actor.Id, AuditCtx);

            Assert.False(result.IsSuccess);
            Assert.IsType<ForbiddenError>(result.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Upload_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var service = new AttachmentService(context, storage, LocalInstanceId);
            var result = await service.UploadAsync(
                new UploadAttachmentRequest(page.Id, "file.txt", "text/plain", Content("data")),
                EditorPrincipal(), actor.Id, AuditCtx);

            Assert.False(result.IsSuccess);
            Assert.IsType<ReadOnlyReplicaError>(result.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // --- Delete ---------------------------------------------------------------------

    [Fact]
    public async Task Delete_Succeeds_SoftDeletes_LeavesBlobInStorage()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var service = new AttachmentService(context, storage, LocalInstanceId);
            var uploaded = await service.UploadAsync(
                new UploadAttachmentRequest(page.Id, "file.txt", "text/plain", Content("data")),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            var result = await service.DeleteAsync(new DeleteAttachmentRequest(uploaded.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);

            Assert.True(result.IsSuccess);
            Assert.True(result.Value.IsDeleted);
            // Soft delete only (data-model.md, mirroring Page's trash pattern) - the
            // blob is left for a future purge job, not removed by this request.
            Assert.True(await storage.ExistsAsync(uploaded.Value.StorageKey, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // --- Download (read side) ---------------------------------------------------------

    [Fact]
    public async Task Download_ViaAttachmentReadService_ReturnsFoundWithCorrectBytes()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var writeService = new AttachmentService(context, storage, LocalInstanceId);
            var fileBytes = "hello world"u8.ToArray();
            var uploaded = await writeService.UploadAsync(
                new UploadAttachmentRequest(page.Id, "hello.txt", "text/plain", new MemoryStream(fileBytes)),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            var readService = new AttachmentReadService(context, storage);
            var download = await readService.DownloadAsync(uploaded.Value.Id, EditorPrincipal());

            var found = Assert.IsType<AttachmentDownloadResult.Found>(download);
            using var reader = new StreamReader(found.Content);
            Assert.Equal("hello world", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Download_AttachmentOnRestrictedPage_ReturnsNotFound_NotForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var writeService = new AttachmentService(context, storage, LocalInstanceId);
            var uploaded = await writeService.UploadAsync(
                new UploadAttachmentRequest(page.Id, "secret.txt", "text/plain", Content("secret")),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            context.AccessRules.Add(ViewRestriction(page.Id, """{ "group": "top-secret" }"""));
            context.SaveChanges();

            var readService = new AttachmentReadService(context, storage);
            var download = await readService.DownloadAsync(uploaded.Value.Id, ViewerOnlyPrincipal());

            // design.md §6.7/§10: absent, not forbidden - no typed "you can't see this"
            // distinct from "this doesn't exist" is exposed to the caller.
            Assert.IsType<AttachmentDownloadResult.NotFound>(download);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Download_NonExistentAttachment_ReturnsNotFound()
    {
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            var readService = new AttachmentReadService(context, storage);

            var download = await readService.DownloadAsync(Guid.NewGuid(), ViewerOnlyPrincipal());

            Assert.IsType<AttachmentDownloadResult.NotFound>(download);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Download_BlobMissingFromStorage_ReturnsBlobMissing_NotA500()
    {
        // design.md §10: "a row whose object is missing surfaces as a flagged error,
        // not a 500" - simulated here by deleting the file out from under a legitimate,
        // viewable row (e.g. the janitor ran, or the disk lost the file).
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var writeService = new AttachmentService(context, storage, LocalInstanceId);
            var uploaded = await writeService.UploadAsync(
                new UploadAttachmentRequest(page.Id, "file.txt", "text/plain", Content("data")),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            await storage.DeleteAsync(uploaded.Value.StorageKey, CancellationToken.None);

            var readService = new AttachmentReadService(context, storage);
            var download = await readService.DownloadAsync(uploaded.Value.Id, EditorPrincipal());

            var blobMissing = Assert.IsType<AttachmentDownloadResult.BlobMissing>(download);
            Assert.Equal(uploaded.Value.Id, blobMissing.Metadata.Id);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
