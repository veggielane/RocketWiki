using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8/§10: the two non-GraphQL attachment routes, run through the same
/// authorization and audit pipeline as GraphQL. "Absent, not forbidden" applies here
/// exactly as it does to Page (§6.7) — an attachment on a page the caller can't view
/// must 404 identically to one that doesn't exist, and a data-integrity fault
/// (BlobMissing) must surface as a structured, logged error, never a raw 500.
/// </summary>
public sealed class AttachmentEndpointTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Guid SpaceId, Guid PageId, Guid CreatorId)> SeedPageAsync(
        string? viewRestrictionNationality = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"ATT{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Attachment Test Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Test Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        if (viewRestrictionNationality is not null)
        {
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction,
                PageId = page.Id,
                Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", [viewRestrictionNationality])),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = creator.Id,
            });
            await db.SaveChangesAsync();
        }

        return (space.Id, page.Id, creator.Id);
    }

    private static HttpClient ClientAs(RocketWikiApiFactory factory, string sub, string? nationality = null) =>
        WithTestUser(factory.CreateClient(), sub, nationality);

    private static HttpClient WithTestUser(HttpClient client, string sub, string? nationality)
    {
        client.SetTestUser(sub: sub, nationality: nationality is null ? null : [nationality]);
        return client;
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        return form;
    }

    [Fact]
    public async Task UploadThenDownload_RoundTripsContent()
    {
        var (_, pageId, _) = await SeedPageAsync();
        var client = ClientAs(factory, $"uploader-{Guid.NewGuid()}");
        var bytes = Encoding.UTF8.GetBytes("hello rocketwiki attachment");

        var uploadResponse = await client.PostAsync($"/attachments/{pageId}", BuildUpload(bytes, "hello.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);

        var uploadJson = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        var attachmentId = uploadJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("hello.txt", uploadJson.RootElement.GetProperty("fileName").GetString());

        var downloadResponse = await client.GetAsync($"/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("text/plain", downloadResponse.Content.Headers.ContentType?.MediaType);
        var downloaded = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, downloaded);
    }

    [Fact]
    public async Task Upload_WithoutCanEdit_ReturnsForbidden_AndPersistsNoRow()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"VWR{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Viewer Only Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = creator.Id };
        db.Spaces.Add(space);
        // Only a Viewer grant - nobody can edit, so nobody can upload.
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = creator.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = creator.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Viewer Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        var client = ClientAs(factory, $"viewer-{Guid.NewGuid()}");
        var response = await client.PostAsync($"/attachments/{page.Id}", BuildUpload([1, 2, 3], "x.bin", "application/octet-stream"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Forbidden", body.RootElement.GetProperty("kind").GetString());

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await verifyDb.Attachments.AnyAsync(a => a.PageId == page.Id));
    }

    [Fact]
    public async Task Upload_Anonymous_ReturnsUnauthorized()
    {
        // RequireAuthorization() rejects a fully anonymous request at the ASP.NET Core
        // pipeline level (401) before MutationAuthHelper's own "Forbidden" check inside
        // the handler is ever reached - a more correct distinction than GraphQL gets to
        // make (no transport-level 401 concept there), not a bug.
        var (_, pageId, _) = await SeedPageAsync();
        var client = factory.CreateClient(); // no SetTestUser - anonymous

        var response = await client.PostAsync($"/attachments/{pageId}", BuildUpload([1], "x.bin", "application/octet-stream"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Download_OfAttachmentOnRestrictedPage_ReturnsNotFound_NotForbidden()
    {
        // Uploaded by a US principal who satisfies the page's view restriction...
        var (_, pageId, _) = await SeedPageAsync(viewRestrictionNationality: "US");
        var uploaderClient = ClientAs(factory, $"us-uploader-{Guid.NewGuid()}", nationality: "US");
        var uploadResponse = await uploaderClient.PostAsync($"/attachments/{pageId}", BuildUpload([9, 9, 9], "secret.bin", "application/octet-stream"));
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var attachmentId = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // ...but an NZ principal, who does not, must see it as absent - not a 403.
        var nzClient = ClientAs(factory, $"nz-viewer-{Guid.NewGuid()}", nationality: "NZ");
        var downloadResponse = await nzClient.GetAsync($"/attachments/{attachmentId}");

        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);
    }

    [Fact]
    public async Task Download_SetsNosniffCacheControlAndETag_AndConditionalRequestGets304()
    {
        var (_, pageId, _) = await SeedPageAsync();
        var client = ClientAs(factory, $"uploader-{Guid.NewGuid()}");
        var uploadResponse = await client.PostAsync(
            $"/attachments/{pageId}", BuildUpload(Encoding.UTF8.GetBytes("cacheable bytes"), "notes.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var attachmentId = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var downloadResponse = await client.GetAsync($"/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);

        // Uploader-supplied bytes under an uploader-supplied content type: the
        // browser must not sniff its way to something more dangerous, and the
        // deliberate Content-Disposition: attachment must survive (it keeps the
        // payload from rendering in-page).
        Assert.Equal("nosniff", Assert.Single(downloadResponse.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("attachment", downloadResponse.Content.Headers.ContentDisposition?.DispositionType);

        // no-cache, not a max-age: every reuse must revalidate through the API so
        // canView and the §7 audit row still happen (see the handler's comment).
        var cacheControl = downloadResponse.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.Private);
        Assert.True(cacheControl.NoCache);

        var etag = downloadResponse.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag!.IsWeak);

        // Conditional request with the returned ETag: framework-handled 304, no
        // body - and STILL an audit row, which is the whole point of no-cache.
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var rowsBefore = await verifyDb.AuditEvents.CountAsync(e =>
            e.Action == "attachment.download" && e.Outcome == AuditOutcome.Success && e.SubjectId == attachmentId);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, $"/attachments/{attachmentId}");
        conditionalRequest.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag.Tag));
        var conditionalResponse = await client.SendAsync(conditionalRequest);

        Assert.Equal(HttpStatusCode.NotModified, conditionalResponse.StatusCode);
        Assert.Empty(await conditionalResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", Assert.Single(conditionalResponse.Headers.GetValues("X-Content-Type-Options")));

        var rowsAfter = await verifyDb.AuditEvents.CountAsync(e =>
            e.Action == "attachment.download" && e.Outcome == AuditOutcome.Success && e.SubjectId == attachmentId);
        Assert.Equal(rowsBefore + 1, rowsAfter);
    }

    [Fact]
    public async Task Download_OfNonexistentAttachment_ReturnsNotFound()
    {
        var client = ClientAs(factory, $"someone-{Guid.NewGuid()}");

        var response = await client.GetAsync($"/attachments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_WithBlobMissingFromStorage_ReturnsStructuredServerError_NotRawException()
    {
        var (spaceId, pageId, creatorId) = await SeedPageAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var attachment = new Attachment
        {
            PageId = pageId,
            FileName = "ghost.bin",
            ContentType = "application/octet-stream",
            SizeBytes = 42,
            ContentHash = new byte[32],
            StorageKey = $"attachments/does-not-exist/{Guid.NewGuid()}", // never written to IFileStorage
            UploadedByUserId = creatorId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        var client = ClientAs(factory, $"someone-{Guid.NewGuid()}");
        var response = await client.GetAsync($"/attachments/{attachment.Id}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Attachment content unavailable", body);
    }
}
