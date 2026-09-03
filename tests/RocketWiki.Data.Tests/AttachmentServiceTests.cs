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
        Kind = AccessRuleKind.RoleGrant,
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
        Kind = AccessRuleKind.AccessGrant,
        SpaceId = spaceId,
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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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

    /// <summary>Refuses every storage call. Anything that authorizes should be able to
    /// run against this untouched.</summary>
    private sealed class ForbiddenFileStorage : IFileStorage
    {
        public Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct) =>
            throw new InvalidOperationException("Storage must not be touched here.");

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("Storage must not be touched here.");

        public Task DeleteAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("Storage must not be touched here.");

        public Task<bool> ExistsAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("Storage must not be touched here.");
    }

    [Fact]
    public async Task ResolveForDownload_AuthorizesWithoutTouchingStorage()
    {
        // The property the conditional-request path depends on. Attachment responses are
        // deliberately no-cache (§10), so every reuse revalidates and an ETag match is
        // the COMMON case — and the route used to open the object before the framework
        // evaluated If-None-Match, paying a HEAD plus a full GET for bytes it then threw
        // away. Answering 304 without touching storage is only correct if authorization
        // is fully decided first, which is exactly what this asserts: a storage that
        // throws on every call, and a resolve that still reaches its verdict.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        var attachment = new Attachment
        {
            PageId = page.Id,
            FileName = "spec.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            ContentHash = new byte[32],
            StorageKey = "attachments/2026/08/spec",
            UploadedByUserId = actor.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.Attachments.Add(attachment);
        context.SaveChanges();

        var readService = new AttachmentReadService(context, new ForbiddenFileStorage());

        var allowed = await readService.ResolveForDownloadAsync(attachment.Id, EditorPrincipal());
        Assert.Equal(attachment.Id, Assert.IsType<AttachmentAccessResult.Allowed>(allowed).Metadata.Id);

        // The denial path must be equally storage-free — more so, in fact: a caller who
        // cannot view the attachment must never cause a storage call on its behalf.
        var restriction = ViewRestriction(page.Id, """{ "group": "top-secret" }""");
        context.AccessRules.Add(restriction);
        context.SaveChanges();

        var denied = await readService.ResolveForDownloadAsync(attachment.Id, ViewerOnlyPrincipal());
        Assert.Equal($"restriction:{page.Id}:{restriction.Id}",
            Assert.IsType<AttachmentAccessResult.Denied>(denied).Reason);

        var missing = await readService.ResolveForDownloadAsync(Guid.NewGuid(), EditorPrincipal());
        Assert.IsType<AttachmentAccessResult.NotFound>(missing);
    }

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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
    public async Task Download_AttachmentOnRestrictedPage_ReturnsDenied_WithFailingRuleReason()
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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
            context.SaveChanges();

            var writeService = new AttachmentService(context, storage, LocalInstanceId);
            var uploaded = await writeService.UploadAsync(
                new UploadAttachmentRequest(page.Id, "secret.txt", "text/plain", Content("secret")),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            var restriction = ViewRestriction(page.Id, """{ "group": "top-secret" }""");
            context.AccessRules.Add(restriction);
            context.SaveChanges();

            var readService = new AttachmentReadService(context, storage);
            var download = await readService.DownloadAsync(uploaded.Value.Id, ViewerOnlyPrincipal());

            // design.md §6.7: internally Denied with the audit-grade failing-restriction
            // reason and nothing else (no Metadata to leak); the HTTP route collapses
            // this to the byte-identical 404 NotFound gets - proven at the HTTP level in
            // RocketWiki.Api.Tests (DeniedReadAuditTests), which is where "absent, not
            // forbidden" is actually enforced now.
            var denied = Assert.IsType<AttachmentDownloadResult.Denied>(download);
            Assert.Equal(uploaded.Value.Id, denied.AttachmentId);
            Assert.Equal($"restriction:{page.Id}:{restriction.Id}", denied.Reason);
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

    /// <summary>
    /// The RACE, which the steady-state test below cannot reach: the object is present
    /// when the read starts and gone by the time the bytes are opened.
    ///
    /// <para>The download path used to ask <c>ExistsAsync</c> and then call
    /// <c>OpenReadAsync</c>, so anything vanishing in that window threw
    /// <c>FileNotFoundException</c> — and nothing in the codebase catches it, so it
    /// surfaced as a raw, unstructured 500, exactly what design.md §10 and the
    /// <c>BlobMissing</c> branch exist to prevent. Modelled with a storage fake that
    /// answers "exists" and then throws, because that IS the observable shape of the
    /// window; reproducing it by real timing would be a flaky way to say the same thing.
    /// The fix removes the window rather than narrowing it: open, and catch.</para>
    /// </summary>
    [Fact]
    public async Task Download_WhenTheBlobVanishesMidRead_IsBlobMissing_NotAnUnhandledException()
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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
            context.SaveChanges();

            var uploaded = await new AttachmentService(context, storage, LocalInstanceId).UploadAsync(
                new UploadAttachmentRequest(page.Id, "file.txt", "text/plain", Content("data")),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            var racing = new VanishingFileStorage(storage);
            var download = await new AttachmentReadService(context, racing)
                .DownloadAsync(uploaded.Value.Id, EditorPrincipal());

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

    /// <summary>Present to <c>ExistsAsync</c>, gone to <c>OpenReadAsync</c> — the window
    /// between a check and a read, made deterministic.</summary>
    private sealed class VanishingFileStorage(IFileStorage inner) : IFileStorage
    {
        public Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct) =>
            inner.SaveAsync(key, content, contentType, ct);

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            throw new FileNotFoundException("The object was deleted between the check and the read.", key);

        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);

        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(true);
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
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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

    // --- Replica read-only (design.md §6.4/§12): Upload refusal is covered above;
    // deleting a synced attachment is equally a replica content mutation.

    [Fact]
    public async Task Delete_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        var attachment = new Attachment
        {
            PageId = page.Id,
            FileName = "mirrored.txt",
            ContentType = "text/plain",
            SizeBytes = 4,
            ContentHash = new byte[32],
            StorageKey = "attachments/mirrored",
            UploadedByUserId = actor.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.Attachments.Add(attachment);
            context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
            context.SaveChanges();

            var service = new AttachmentService(context, storage, LocalInstanceId);
            var result = await service.DeleteAsync(new DeleteAttachmentRequest(attachment.Id), EditorPrincipal(), actor.Id, AuditCtx);

            Assert.False(result.IsSuccess);
            var error = Assert.IsType<ReadOnlyReplicaError>(result.Error);
            Assert.Equal("some-other-instance", error.OriginInstanceId);
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
