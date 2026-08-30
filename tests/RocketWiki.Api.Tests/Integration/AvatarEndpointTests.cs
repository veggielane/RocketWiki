using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The profile-picture write/read routes (design: profile pictures): upload
/// normalization (any accepted format in, canonical 512×512 PNG out — the original
/// bytes, EXIF included, never stored), validation refusals, structural self-only,
/// clear, ETag/304, audit rows via the domain-event pipeline, and the
/// UserRef/CurrentUser <c>hasAvatar</c> contract phase 2 codes against.
/// </summary>
public sealed class AvatarEndpointTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    // --- image fixtures -----------------------------------------------------

    internal static byte[] MakePng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 30, 30));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    /// <summary>Deterministic noise, so deflate can't compress it under a byte cap
    /// the test needs it to exceed (a solid-color PNG compresses to almost nothing).</summary>
    private static byte[] MakeNoisyPng(int width, int height)
    {
        var random = new Random(42);
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static byte[] MakeJpegWithExif(int width, int height, string exifSentinel)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(30, 30, 200));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Artist, exifSentinel);
        exif.SetValue(ExifTag.ImageDescription, exifSentinel);
        image.Metadata.ExifProfile = exif;
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder());
        return stream.ToArray();
    }

    internal static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private HttpClient ClientAs(string sub, string? email = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, email: email, name: $"Avatar {sub}");
        return client;
    }

    private async Task<Guid> LocalUserIdOf(string sub)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.Users.Where(u => u.Subject == sub).Select(u => u.Id).SingleAsync();
    }

    // --- upload round trip and normalization --------------------------------

    [Fact]
    public async Task Upload_NormalizesToCanonical512Png_AndServesItWithNosniffAndETag()
    {
        var sub = $"av-rt-{Guid.NewGuid():N}";
        var client = ClientAs(sub, email: $"{sub}@example.test");

        // Deliberately NOT 512×512 and not even square: the server, not the client,
        // owes the canonical form (the SPA cropper is UX sugar, not load-bearing).
        var upload = MakePng(800, 600);
        var response = await client.PostAsync("/avatars", BuildUpload(upload, "me.png", "image/png"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("hasAvatar").GetBoolean());

        var userId = await LocalUserIdOf(sub);
        var get = await client.GetAsync($"/users/{userId}/avatar");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(get.Headers.GetValues("X-Content-Type-Options")));
        Assert.NotNull(get.Headers.ETag);

        var served = await get.Content.ReadAsByteArrayAsync();
        // The library-independent invariant: what is served is a well-formed PNG
        // declaring exactly the canonical square...
        Assert.True(PngHeader.IsAvatarPng(served));
        // ...and is a server re-encode, never the uploaded original.
        Assert.NotEqual(upload, served);
    }

    [Fact]
    public async Task Upload_AcceptsJpeg_AndStripsItsExifMetadata()
    {
        const string exifSentinel = "ZZEXIFSENTINELGPSZZ";
        var sub = $"av-exif-{Guid.NewGuid():N}";
        var client = ClientAs(sub);

        var upload = MakeJpegWithExif(700, 700, exifSentinel);
        Assert.Contains(exifSentinel, Encoding.Latin1.GetString(upload)); // non-vacuous: the sentinel IS in the upload

        var response = await client.PostAsync("/avatars", BuildUpload(upload, "photo.jpg", "image/jpeg"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var userId = await LocalUserIdOf(sub);
        var served = await (await client.GetAsync($"/users/{userId}/avatar")).Content.ReadAsByteArrayAsync();
        Assert.True(PngHeader.IsAvatarPng(served));
        // The privacy property: no source metadata survives normalization.
        Assert.DoesNotContain(exifSentinel, Encoding.Latin1.GetString(served));
    }

    // --- validation refusals -------------------------------------------------

    [Theory]
    [InlineData("not an image at all, just text bytes", "note.png")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""", "sneaky.png")]
    public async Task Upload_OfNonImageBytes_IsARefusal_WithNothingPersisted(string content, string fileName)
    {
        var sub = $"av-bad-{Guid.NewGuid():N}";
        var client = ClientAs(sub);

        var response = await client.PostAsync(
            "/avatars", BuildUpload(Encoding.UTF8.GetBytes(content), fileName, "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Validation", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("kind").GetString());

        var userId = await LocalUserIdOf(sub);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.UserAvatars.AnyAsync(a => a.UserId == userId));
        Assert.False(await db.AuditEvents.AnyAsync(e => e.UserId == userId)); // no access decision was made
    }

    [Fact]
    public async Task Upload_DeclaringAbsurdDimensions_IsRefusedByTheHeaderGate()
    {
        var sub = $"av-dim-{Guid.NewGuid():N}";
        var client = ClientAs(sub);

        // A real, decodable PNG whose width exceeds the processor's per-side cap —
        // tall-thin so the test itself stays tiny.
        var oversized = MakePng(Api.Avatars.ImageSharpAvatarProcessor.MaxSourceDimension + 1, 4);
        var response = await client.PostAsync("/avatars", BuildUpload(oversized, "wide.png", "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Validation", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Upload_OverTheByteCap_Is413_BeforeAnythingIsWritten()
    {
        // Tiny configured cap so "over" doesn't mean megabytes in a test — same
        // approach as AttachmentSizeLimitTests, same shared SQLite file/storage root.
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Avatars:MaxSizeBytes"] = "1024" })));

        var sub = $"av-cap-{Guid.NewGuid():N}";
        var client = host.CreateClient();
        client.SetTestUser(sub: sub);

        var overCap = MakeNoisyPng(64, 64); // incompressible, comfortably over 1 KiB
        Assert.True(overCap.Length > 1024);

        var response = await client.PostAsync("/avatars", BuildUpload(overCap, "big.png", "image/png"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("maxSizeBytes", await response.Content.ReadAsStringAsync());
    }

    // --- self-only, clear, audit --------------------------------------------

    [Fact]
    public async Task SelfOnly_AUploadCannotTouchB_AndBCannotClearA()
    {
        var subA = $"av-a-{Guid.NewGuid():N}";
        var subB = $"av-b-{Guid.NewGuid():N}";
        var clientA = ClientAs(subA);
        var clientB = ClientAs(subB);

        // Both users exist before A uploads (B makes a request), so "A's upload
        // landed on A only" is checkable against a real B row.
        await clientB.PostGraphQLAsync("{ me { localUserId } }");

        Assert.Equal(HttpStatusCode.OK,
            (await clientA.PostAsync("/avatars", BuildUpload(MakePng(512, 512), "a.png", "image/png"))).StatusCode);

        var userA = await LocalUserIdOf(subA);
        var userB = await LocalUserIdOf(subB);

        // The route has no target-user parameter: A's upload can only have landed on
        // A. B has no avatar anywhere - row, GET, or graph flag.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            Assert.True(await db.UserAvatars.AnyAsync(a => a.UserId == userA));
            Assert.False(await db.UserAvatars.AnyAsync(a => a.UserId == userB));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/users/{userB}/avatar")).StatusCode);
        Assert.False((await clientB.PostGraphQLAsync("{ me { hasAvatar } }"))
            .RootElement.GetProperty("data").GetProperty("me").GetProperty("hasAvatar").GetBoolean());

        // B "clearing" clears B's (nonexistent) avatar - a validation refusal that
        // leaves A's avatar exactly where it was.
        Assert.Equal(HttpStatusCode.BadRequest, (await clientB.DeleteAsync("/avatars")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync($"/users/{userA}/avatar")).StatusCode);
    }

    [Fact]
    public async Task SetThenClear_RoundTrip_BothAudited_OnTheAttachmentChannel_WithNoDetails()
    {
        var sub = $"av-audit-{Guid.NewGuid():N}";
        var client = ClientAs(sub);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("/avatars", BuildUpload(MakePng(512, 512), "a.png", "image/png"))).StatusCode);

        var userId = await LocalUserIdOf(sub);

        var clear = await client.DeleteAsync("/avatars");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.False(JsonDocument.Parse(await clear.Content.ReadAsStringAsync())
            .RootElement.GetProperty("hasAvatar").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/users/{userId}/avatar")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.UserAvatars.AnyAsync(a => a.UserId == userId));

        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.Action.StartsWith("settings.avatar"))
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Action == "settings.avatar.set");
        Assert.Single(rows, r => r.Action == "settings.avatar.cleared");
        Assert.All(rows, r =>
        {
            Assert.Equal(AuditOutcome.Success, r.Outcome);
            Assert.Equal(AuditChannel.Attachment, r.Channel);
            Assert.Null(r.DetailsJson); // nothing about the image near a row
            Assert.Null(r.SubjectId);
        });
    }

    [Fact]
    public async Task Get_WithMatchingIfNoneMatch_Returns304_WithoutBytes()
    {
        var sub = $"av-etag-{Guid.NewGuid():N}";
        var client = ClientAs(sub);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("/avatars", BuildUpload(MakePng(512, 512), "a.png", "image/png"))).StatusCode);
        var userId = await LocalUserIdOf(sub);

        var first = await client.GetAsync($"/users/{userId}/avatar");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/users/{userId}/avatar");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Get_Anonymous_IsUnauthorized_AndUnknownUserIs404()
    {
        var anonymous = factory.CreateClient(); // no SetTestUser
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/users/{Guid.NewGuid()}/avatar")).StatusCode);

        var client = ClientAs($"av-404-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/users/{Guid.NewGuid()}/avatar")).StatusCode);
    }

    // --- the GraphQL contract phase 2 codes against -------------------------

    [Fact]
    public async Task HasAvatar_FlipsOnMe_AndOnUserRef_AfterUpload()
    {
        var sub = $"av-gql-{Guid.NewGuid():N}";
        var client = ClientAs(sub);

        Assert.False((await client.PostGraphQLAsync("{ me { hasAvatar } }"))
            .RootElement.GetProperty("data").GetProperty("me").GetProperty("hasAvatar").GetBoolean());

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("/avatars", BuildUpload(MakePng(512, 512), "a.png", "image/png"))).StatusCode);

        Assert.True((await client.PostGraphQLAsync("{ me { hasAvatar } }"))
            .RootElement.GetProperty("data").GetProperty("me").GetProperty("hasAvatar").GetBoolean());

        // UserRef.hasAvatar through a real nested path: the uploader as a comment
        // author on a viewable page.
        var userId = await LocalUserIdOf(sub);
        Guid pageId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var space = new Space
            {
                Key = $"AVG{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "Avatar GraphQL Space",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = userId,
            };
            db.Spaces.Add(space);
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.SpaceGrant,
                SpaceId = space.Id,
                Role = SpaceRole.Viewer,
                ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = userId,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = userId,
            });
            var page = new Page
            {
                SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Avatar Page",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            };
            db.Pages.Add(page);
            db.Comments.Add(new Comment
            {
                PageId = page.Id, Body = "by someone with an avatar",
                AuthorUserId = userId, CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            pageId = page.Id;
        }

        var result = await client.PostGraphQLAsync(
            $$"""{ page(id: "{{pageId}}") { comments { author { displayName hasAvatar } } } }""");
        var author = result.RootElement.GetProperty("data").GetProperty("page")
            .GetProperty("comments")[0].GetProperty("author");
        Assert.True(author.GetProperty("hasAvatar").GetBoolean());
    }
}
