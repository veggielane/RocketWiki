using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The custom-emoji feature end to end: admin-gated registry mutations on /emojis
/// (audited emoji.created / emoji.deleted, denials included), the authenticated
/// serve route with its cache contract (ETag/304, nosniff), image normalization
/// (decode-limited, squared, re-encoded — never the caller's bytes), the :name:
/// grammar, and the customEmojis GraphQL list. Names are GUID-suffixed because the
/// class shares one factory (and so one SQLite file) across tests.
/// </summary>
public sealed class CustomEmojiEndpointTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private static string UniqueName(string prefix = "emoji") => $"{prefix}-{Guid.NewGuid():N}";

    private HttpClient AdminClient() =>
        ClientAs($"admin-{Guid.NewGuid()}", roles: ["admin"]);

    private HttpClient UserClient() =>
        ClientAs($"user-{Guid.NewGuid()}");

    private HttpClient ClientAs(string sub, IEnumerable<string>? roles = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, roles: roles);
        return client;
    }

    // --- image builders (ImageSharp is available transitively via the Api project) ---

    private static byte[] Encode(Image<Rgba32> image, IImageEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 30, 30, 255));
        return Encode(image, new PngEncoder());
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(30, 200, 30, 255));
        return Encode(image, new JpegEncoder());
    }

    private static byte[] Bmp(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(30, 30, 200, 255));
        return Encode(image, new BmpEncoder());
    }

    private static byte[] AnimatedGif(int frames, int size = 64)
    {
        using var image = new Image<Rgba32>(size, size, new Rgba32(255, 0, 0, 255));
        for (var i = 1; i < frames; i++)
        {
            using var frame = new Image<Rgba32>(size, size, new Rgba32((byte)(i * 3 % 256), 255, 0, 255));
            image.Frames.AddFrame(frame.Frames.RootFrame);
        }

        return Encode(image, new GifEncoder());
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string name, byte[] bytes) =>
        client.PostAsync($"/emojis/{name}", new ByteArrayContent(bytes));

    private async Task<JsonDocument> CreateEmojiAsync(HttpClient adminClient, string name, byte[]? bytes = null)
    {
        var response = await UploadAsync(adminClient, name, bytes ?? Png(64, 64));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------
    // Round trip, serving, and the cache contract
    // ------------------------------------------------------------------

    [Fact]
    public async Task AdminCreate_ThenServe_RoundTrips_WithEtagAndNosniff()
    {
        var admin = AdminClient();
        var name = UniqueName();
        var created = await CreateEmojiAsync(admin, name, Png(64, 64));
        Assert.Equal("image/png", created.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(64, created.RootElement.GetProperty("pixelSize").GetInt32());
        var etagFromCreate = created.RootElement.GetProperty("etag").GetString();
        Assert.False(string.IsNullOrEmpty(etagFromCreate));

        var viewer = UserClient(); // serving needs authentication only, not admin
        var response = await viewer.GetAsync($"/emojis/{name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(etagFromCreate, response.Headers.ETag?.ToString());

        // The served bytes are the server's re-encode: a decodable 64x64 PNG.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var served = Image.Load<Rgba32>(bytes);
        Assert.Equal(64, served.Width);
        Assert.Equal(64, served.Height);
    }

    [Fact]
    public async Task Serve_WithMatchingIfNoneMatch_Returns304WithoutBody()
    {
        var admin = AdminClient();
        var name = UniqueName();
        await CreateEmojiAsync(admin, name);

        var viewer = UserClient();
        var first = await viewer.GetAsync($"/emojis/{name}");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/emojis/{name}");
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag!.ToString()));
        var second = await viewer.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(etag.ToString(), second.Headers.ETag?.ToString());
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Serve_UnknownName_ReturnsNotFound()
    {
        var viewer = UserClient();

        var response = await viewer.GetAsync($"/emojis/{UniqueName("never-created")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Serve_Anonymous_ReturnsUnauthorized()
    {
        var response = await factory.CreateClient().GetAsync("/emojis/anything");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // The admin gate, with its audited denials (§7 mutation-denial pattern)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_AsNonAdmin_ReturnsForbidden_AuditsDenial_AndPersistsNothing()
    {
        var name = UniqueName("denied");
        var response = await UploadAsync(UserClient(), name, Png(64, 64));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Forbidden", body.RootElement.GetProperty("kind").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.CustomEmojis.AnyAsync(e => e.Name == name));

        var denial = await db.AuditEvents.SingleAsync(
            e => e.Action == "emoji.created" && e.Outcome == AuditOutcome.Denied && e.DetailsJson!.Contains(name));
        Assert.Equal(AuditChannel.Attachment, denial.Channel);
        Assert.Contains("instance admin required", denial.DetailsJson);
    }

    [Fact]
    public async Task Delete_AsNonAdmin_ReturnsForbidden_AuditsDenial_AndDeletesNothing()
    {
        var admin = AdminClient();
        var name = UniqueName("keepme");
        await CreateEmojiAsync(admin, name);

        var response = await UserClient().DeleteAsync($"/emojis/{name}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.True(await db.CustomEmojis.AnyAsync(e => e.Name == name));
        Assert.True(await db.AuditEvents.AnyAsync(
            e => e.Action == "emoji.deleted" && e.Outcome == AuditOutcome.Denied && e.DetailsJson!.Contains(name)));
    }

    // ------------------------------------------------------------------
    // Grammar: lowercase [a-z0-9_-], 1-64 - everything else refused, unaudited
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Banana")] // uppercase
    [InlineData("ba nana")] // space (arrives URL-decoded)
    [InlineData("ban:ana")] // ':' - the delimiter can never be part of a name
    public async Task Create_WithGrammarViolatingName_ReturnsValidation(string name)
    {
        var response = await UploadAsync(AdminClient(), name, Png(64, 64));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Validation", body.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Create_With65CharName_ReturnsValidation_And64CharNameWorks()
    {
        var admin = AdminClient();

        var tooLong = await UploadAsync(admin, new string('a', 65), Png(64, 64));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        // 64 exactly is inside the grammar - the cap is inclusive. GUID-suffixed so
        // the shared fixture can rerun; padded to exactly 64 with 'a'.
        var name64 = ($"cap-{Guid.NewGuid():N}" + new string('a', 64))[..64];
        var atCap = await UploadAsync(admin, name64, Png(64, 64));
        Assert.Equal(HttpStatusCode.Created, atCap.StatusCode);
    }

    [Fact]
    public async Task Create_CaseVariantOfExistingName_IsRefusedByGrammar_NeverASecondRow()
    {
        var admin = AdminClient();
        var name = UniqueName("banana");
        await CreateEmojiAsync(admin, name);

        // Case-insensitive uniqueness is structural: the uppercase variant is refused
        // as a grammar violation (lowercase-only), so no case variant can ever create
        // a second registry row - and the lookup stays exact/ordinal.
        var upper = await UploadAsync(admin, name.ToUpperInvariant(), Png(64, 64));
        Assert.Equal(HttpStatusCode.BadRequest, upper.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(1, await db.CustomEmojis.CountAsync(e => e.Name.ToLower() == name));
    }

    [Fact]
    public async Task Create_DuplicateName_ReturnsNameTakenConflict()
    {
        var admin = AdminClient();
        var name = UniqueName("dupe");
        await CreateEmojiAsync(admin, name);

        var second = await UploadAsync(admin, name, Png(48, 48));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("NameTaken", body.RootElement.GetProperty("kind").GetString());
    }

    // ------------------------------------------------------------------
    // Image validation and normalization
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_WithNonImageBytes_ReturnsValidation()
    {
        var response = await UploadAsync(AdminClient(), UniqueName(), Encoding.UTF8.GetBytes("not an image at all"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Validation", body.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Create_WithSvgBytes_ReturnsValidation_SvgIsNeverAnEmoji()
    {
        var svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""");

        var response = await UploadAsync(AdminClient(), UniqueName(), svg);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithBmp_ReturnsValidation_FormatAllowlistIsStructural()
    {
        // A perfectly valid image in a format outside the PNG/JPEG/WebP/GIF allowlist -
        // the restricted decoder configuration has no BMP decoder, so this fails as
        // unknown-format, not as a forgotten check.
        var response = await UploadAsync(AdminClient(), UniqueName(), Bmp(64, 64));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_TooSmall_ReturnsValidation()
    {
        var response = await UploadAsync(AdminClient(), UniqueName(), Png(16, 16));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_512Square_IsAccepted_AndDownscaledTo256()
    {
        var admin = AdminClient();
        var name = UniqueName("big");
        var created = await CreateEmojiAsync(admin, name, Png(512, 512));

        Assert.Equal(256, created.RootElement.GetProperty("pixelSize").GetInt32());

        var served = await admin.GetAsync($"/emojis/{name}");
        using var image = Image.Load<Rgba32>(await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
    }

    [Fact]
    public async Task Create_NonSquareWithinAspectLimit_IsPaddedToSquare()
    {
        var admin = AdminClient();
        var name = UniqueName("wide");
        var created = await CreateEmojiAsync(admin, name, Png(64, 32)); // ratio 2.0 - the inclusive limit

        Assert.Equal(64, created.RootElement.GetProperty("pixelSize").GetInt32());

        var served = await admin.GetAsync($"/emojis/{name}");
        using var image = Image.Load<Rgba32>(await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        // Centered pad: the strip above the original content is transparent.
        Assert.Equal(0, image[32, 2].A);
    }

    [Fact]
    public async Task Create_AbsurdAspectRatio_ReturnsValidation()
    {
        var response = await UploadAsync(AdminClient(), UniqueName(), Png(200, 20)); // 10:1

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_OversizeUpload_ReturnsStructured413()
    {
        var response = await UploadAsync(AdminClient(), UniqueName(), new byte[300_000]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("maxSizeBytes", body);
    }

    [Fact]
    public async Task Create_Jpeg_IsReencodedAndServedAsPng()
    {
        var admin = AdminClient();
        var name = UniqueName("photo");
        var created = await CreateEmojiAsync(admin, name, Jpeg(64, 64));
        Assert.Equal("image/png", created.RootElement.GetProperty("contentType").GetString());

        var served = await admin.GetAsync($"/emojis/{name}");
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_AnimatedGif_PreservesAnimation_ServedAsGif()
    {
        var admin = AdminClient();
        var name = UniqueName("dance");
        var created = await CreateEmojiAsync(admin, name, AnimatedGif(frames: 3));
        Assert.Equal("image/gif", created.RootElement.GetProperty("contentType").GetString());

        var served = await admin.GetAsync($"/emojis/{name}");
        Assert.Equal("image/gif", served.Content.Headers.ContentType?.MediaType);
        using var image = Image.Load<Rgba32>(await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, image.Frames.Count);
    }

    [Fact]
    public async Task Create_GifOverFrameCap_ReturnsValidation()
    {
        var response = await UploadAsync(AdminClient(), UniqueName(), AnimatedGif(frames: 70, size: 32));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("frames", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Create_ReencodeStripsMetadata()
    {
        using var source = new Image<Rgba32>(64, 64, new Rgba32(1, 2, 3, 255));
        source.Metadata.GetPngMetadata().TextData.Add(
            new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("comment", "SENTINEL-METADATA", string.Empty, string.Empty));
        byte[] upload;
        using (var stream = new MemoryStream())
        {
            source.Save(stream, new PngEncoder());
            upload = stream.ToArray();
        }

        var admin = AdminClient();
        var name = UniqueName("clean");
        await CreateEmojiAsync(admin, name, upload);

        var servedBytes = await (await admin.GetAsync($"/emojis/{name}")).Content.ReadAsByteArrayAsync();
        Assert.NotEqual(upload, servedBytes); // never the caller's bytes
        using var served = Image.Load<Rgba32>(servedBytes);
        Assert.Empty(served.Metadata.GetPngMetadata().TextData);
    }

    // ------------------------------------------------------------------
    // Delete, re-create, and the registry lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_ThenServe404s_AndRecreateWorks()
    {
        var admin = AdminClient();
        var name = UniqueName("phoenix");
        await CreateEmojiAsync(admin, name);

        var delete = await admin.DeleteAsync($"/emojis/{name}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await admin.GetAsync($"/emojis/{name}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        // Hard delete means the name is immediately reusable - no tombstone blocks it.
        await CreateEmojiAsync(admin, name, Png(48, 48));
        var afterRecreate = await admin.GetAsync($"/emojis/{name}");
        Assert.Equal(HttpStatusCode.OK, afterRecreate.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownName_ReturnsNotFound()
    {
        var response = await AdminClient().DeleteAsync($"/emojis/{UniqueName("ghost")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Audit (§7): mutations audited via the domain-event pipeline, GETs not at all
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAndDelete_WriteAuditRows_ServeWritesNone()
    {
        var admin = AdminClient();
        var name = UniqueName("audited");
        await CreateEmojiAsync(admin, name);
        await admin.GetAsync($"/emojis/{name}");
        await UserClient().GetAsync($"/emojis/{name}");
        var delete = await admin.DeleteAsync($"/emojis/{name}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var rows = await db.AuditEvents
            .Where(e => e.DetailsJson != null && e.DetailsJson.Contains(name))
            .OrderBy(e => e.Id)
            .ToListAsync();

        // Exactly two rows mention this emoji: the create and the delete. The two
        // GETs (display-asset serving) added nothing - if they had, this count fails.
        Assert.Equal(2, rows.Count);

        var created = rows[0];
        Assert.Equal("emoji.created", created.Action);
        Assert.Equal(AuditOutcome.Success, created.Outcome);
        Assert.Equal(AuditChannel.Attachment, created.Channel);
        Assert.NotNull(created.UserId);
        Assert.Contains($"\"name\":\"{name}\"", created.DetailsJson);

        var deleted = rows[1];
        Assert.Equal("emoji.deleted", deleted.Action);
        Assert.Equal(AuditOutcome.Success, deleted.Outcome);
        Assert.Contains($"\"name\":\"{name}\"", deleted.DetailsJson);
    }

    // ------------------------------------------------------------------
    // customEmojis GraphQL list - the picker/renderer's known-names set
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomEmojisQuery_ListsNamesAndEtags_ForAnyAuthenticatedUser()
    {
        var admin = AdminClient();
        var name = UniqueName("listed");
        var created = await CreateEmojiAsync(admin, name);
        var expectedEtag = created.RootElement.GetProperty("etag").GetString();

        var result = await UserClient().PostGraphQLAsync("{ customEmojis { name etag } }");

        var entries = result.RootElement.GetProperty("data").GetProperty("customEmojis").EnumerateArray()
            .Select(e => (Name: e.GetProperty("name").GetString(), Etag: e.GetProperty("etag").GetString()))
            .ToList();
        var entry = Assert.Single(entries, e => e.Name == name);
        Assert.Equal(expectedEtag, entry.Etag);
    }

    [Fact]
    public async Task CustomEmojisQuery_Anonymous_GetsEmptyList()
    {
        var admin = AdminClient();
        await CreateEmojiAsync(admin, UniqueName("hidden"));

        var result = await factory.CreateClient().PostGraphQLAsync("{ customEmojis { name } }");

        Assert.Empty(result.RootElement.GetProperty("data").GetProperty("customEmojis").EnumerateArray());
    }
}
