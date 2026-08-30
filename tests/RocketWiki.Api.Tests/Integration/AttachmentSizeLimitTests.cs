using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using RocketWiki.Storage;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The API-side attachment size limit (Attachments:MaxSizeBytes; AttachmentOptions has
/// the why). Proven the way this repo proves guards - by breaking it: one byte over the
/// configured limit is a structured 413 with nothing persisted anywhere (no Attachment
/// row, no blob in IFileStorage, no audit row - a refused-too-big upload made no access
/// decision, see the route's comment), while exactly the limit sails through. Runs
/// against a host configured with a tiny limit so "over" doesn't mean buffering 100 MiB
/// in a unit test; the default value itself is pinned separately below.
/// </summary>
public sealed class AttachmentSizeLimitTests : IClassFixture<RocketWikiApiFactory>
{
    private const long ConfiguredLimitBytes = 1024;

    /// <summary>Same SQLite file and FileSystem storage root as the shared fixture
    /// (its ConfigureWebHost still runs; WithWebHostBuilder's delegate is applied on
    /// top), with only the limit overridden to something a test can exceed.</summary>
    private readonly WebApplicationFactory<Program> _host;

    public AttachmentSizeLimitTests(RocketWikiApiFactory factory) =>
        _host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Attachments:MaxSizeBytes"] = ConfiguredLimitBytes.ToString() })));

    private async Task<Guid> SeedEditablePageAsync()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        var space = new Space
        {
            Key = $"SZL{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Size Limit Space",
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
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Size Limit Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private int CountStoredBlobs()
    {
        // The FileSystem provider's root for this host - counting files there is the
        // honest "no blob persisted" check, independent of what the DB says.
        using var scope = _host.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<IOptions<FileStorageOptions>>()
            .Value.FileSystem?.Root;
        return root is not null && Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private async Task<int> TotalAuditRowsAsync()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents.CountAsync();
    }

    [Fact]
    public async Task Upload_OneByteOverTheLimit_Returns413Problem_AndPersistsNothing()
    {
        var pageId = await SeedEditablePageAsync();
        var client = _host.CreateClient();
        client.SetTestUser(sub: $"uploader-{Guid.NewGuid()}");
        var blobsBefore = CountStoredBlobs();
        var auditRowsBefore = await TotalAuditRowsAsync();

        var response = await client.PostAsync(
            $"/attachments/{pageId}", BuildUpload(new byte[ConfiguredLimitBytes + 1], "too-big.bin"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Attachment too large", problem.GetProperty("title").GetString());
        Assert.Equal(ConfiguredLimitBytes, problem.GetProperty("maxSizeBytes").GetInt64());

        // Nothing persisted anywhere: no row, no blob, and - the §7 decision proven -
        // no audit row either (no access decision was made; same as this route's
        // NotFound/Validation refusals, per the comment in UploadAsync).
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.Attachments.AnyAsync(a => a.PageId == pageId));
        Assert.Equal(blobsBefore, CountStoredBlobs());
        Assert.Equal(auditRowsBefore, await TotalAuditRowsAsync());
    }

    /// <summary>
    /// The layer that had no test and, under TestServer, no working backstop: a body that
    /// declares no Content-Length. Layer 1 (the declared-length refusal) cannot fire, and
    /// layer 2 (Kestrel's IHttpMaxRequestBodySizeFeature) does not exist here — so what
    /// refuses this is the in-handler read bound BinaryRoutes gives all three binary
    /// routes. It answers with the same structured 413 as every other over-limit upload;
    /// before the consolidation the multipart reader's own exception escaped the handler
    /// instead. The avatar route runs the identical helper.
    /// </summary>
    [Fact]
    public async Task Upload_WithNoDeclaredContentLength_IsStillARefusal_NotAnUnhandledFailure()
    {
        var pageId = await SeedEditablePageAsync();
        var client = _host.CreateClient();
        client.SetTestUser(sub: $"uploader-{Guid.NewGuid()}");
        var blobsBefore = CountStoredBlobs();
        var auditRowsBefore = await TotalAuditRowsAsync();

        // Big enough to pass the multipart envelope allowance too, so the reader's bound
        // is what stops it rather than the file-length check afterwards.
        var oversize = new byte[ConfiguredLimitBytes + RocketWiki.Api.Http.BinaryRoutes.MultipartEnvelopeAllowanceBytes + 1];
        var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(new UnknownLengthStream(oversize));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "no-content-length.bin");

        // The premise, pinned: an unknown-length part means the whole body has no
        // computable Content-Length, so the up-front refusal genuinely cannot apply.
        Assert.Null(form.Headers.ContentLength);

        var response = await client.PostAsync($"/attachments/{pageId}", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Attachment too large", problem.GetProperty("title").GetString());
        Assert.Equal(ConfiguredLimitBytes, problem.GetProperty("maxSizeBytes").GetInt64());

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.Attachments.AnyAsync(a => a.PageId == pageId));
        Assert.Equal(blobsBefore, CountStoredBlobs());
        Assert.Equal(auditRowsBefore, await TotalAuditRowsAsync());
    }

    /// <summary>Forward-only, length-unknown stream: <see cref="StreamContent"/> cannot
    /// compute a length for a non-seekable stream, which is what makes the multipart body
    /// above arrive without a Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Upload_ExactlyAtTheLimit_IsAccepted_AndRoundTrips()
    {
        var pageId = await SeedEditablePageAsync();
        var client = _host.CreateClient();
        client.SetTestUser(sub: $"uploader-{Guid.NewGuid()}");
        var atLimit = new byte[ConfiguredLimitBytes];
        Random.Shared.NextBytes(atLimit);

        var response = await client.PostAsync($"/attachments/{pageId}", BuildUpload(atLimit, "at-limit.bin"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ConfiguredLimitBytes, created.GetProperty("sizeBytes").GetInt64());

        var download = await client.GetAsync($"/attachments/{created.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(atLimit, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void DefaultLimit_Matches_TheNginxCapItReplacesAsTheSystemCap()
    {
        // AttachmentOptions' doc: the default deliberately equals nginx's
        // client_max_body_size 100m (1024-based) so the API-side cap and the proxy
        // cap agree out of the box. Pinned so a drive-by "round it to 10^8" edit
        // fails a test instead of silently diverging from the deployed proxy line.
        Assert.Equal(104_857_600, RocketWiki.Api.Attachments.AttachmentOptions.DefaultMaxSizeBytes);
    }
}
