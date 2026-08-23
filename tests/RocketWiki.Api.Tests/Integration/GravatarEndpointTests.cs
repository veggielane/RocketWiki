using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Content;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The Gravatar/Libravatar-protocol endpoint (design: profile pictures):
/// fail-closed flag (default off means 404 for everything), lookup by MD5 AND
/// SHA-256 of the normalized email with case-insensitive hashes, <c>d=404</c> as the
/// only default behavior, <c>s=</c> honored with clamping, anonymity (no audit rows,
/// no JIT row, no principal needed), and the JIT email-drift rehash that keeps a
/// hash from ever serving the wrong person.
/// </summary>
public sealed class GravatarEndpointTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    /// <summary>Same SQLite file and storage root as the shared fixture, with only
    /// the opt-in flag turned on — uploads made through either host are visible to
    /// both, which is exactly how the disabled/enabled contrast tests want it.</summary>
    private WebApplicationFactory<Program> EnabledHost() =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Avatars:GravatarEndpointEnabled"] = "true" })));

    /// <summary>Uploads an avatar as a fresh user with <paramref name="email"/> and
    /// returns both stored email hashes.</summary>
    private async Task<(string Sub, string Md5, string Sha256)> SeedAvatarUserAsync(string email)
    {
        var sub = $"grav-{Guid.NewGuid():N}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, email: email, name: "Gravatar User");

        var response = await client.PostAsync("/avatars",
            AvatarEndpointTests.BuildUpload(AvatarEndpointTests.MakePng(512, 512), "a.png", "image/png"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (md5, sha256) = AvatarEmailHasher.Compute(email);
        return (sub, md5!, sha256!);
    }

    [Fact]
    public async Task Disabled_ByDefault_Returns404ForEverything_EvenARealHash()
    {
        var (_, md5, sha256) = await SeedAvatarUserAsync($"disabled-{Guid.NewGuid():N}@example.test");
        var anonymous = factory.CreateClient(); // default host: flag off, no auth

        // The real hashes, an unknown hash, and garbage are all the same 404 - a
        // probe cannot learn whether the feature is even enabled.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{md5}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{sha256}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{new string('0', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/avatar/nothash")).StatusCode);
    }

    [Fact]
    public async Task Enabled_ServesByMd5AndSha256_CaseInsensitively_Anonymously()
    {
        var (_, md5, sha256) = await SeedAvatarUserAsync($"hit-{Guid.NewGuid():N}@example.test");
        using var host = EnabledHost();
        var anonymous = host.CreateClient(); // never SetTestUser - the whole point

        foreach (var hash in new[] { md5, sha256, md5.ToUpperInvariant(), sha256.ToUpperInvariant() })
        {
            var response = await anonymous.GetAsync($"/avatar/{hash}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Contains("public", response.Headers.CacheControl?.ToString());
            Assert.NotNull(response.Headers.ETag);
            Assert.True(PngHeader.IsAvatarPng(await response.Content.ReadAsByteArrayAsync()));
        }
    }

    [Fact]
    public async Task Enabled_UnknownHash_AvatarlessUser_AndGarbage_AllReturn404()
    {
        // An avatar-less user WITH an email: their hash must 404 exactly like a hash
        // belonging to nobody.
        var avatarlessEmail = $"noavatar-{Guid.NewGuid():N}@example.test";
        var avatarlessClient = factory.CreateClient();
        avatarlessClient.SetTestUser(sub: $"grav-none-{Guid.NewGuid():N}", email: avatarlessEmail);
        await avatarlessClient.PostGraphQLAsync("{ me { localUserId } }"); // JIT the user row
        var (noAvatarMd5, _) = AvatarEmailHasher.Compute(avatarlessEmail);

        using var host = EnabledHost();
        var anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{noAvatarMd5}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{new string('a', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{new string('a', 40)}")).StatusCode); // SHA-1 shaped: not a protocol hash
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/avatar/zz'; DROP TABLE--")).StatusCode);
    }

    [Fact]
    public async Task DefaultAndSizeParameters_BehaveAsDocumented()
    {
        var (_, md5, _) = await SeedAvatarUserAsync($"params-{Guid.NewGuid():N}@example.test");
        using var host = EnabledHost();
        var anonymous = host.CreateClient();

        // d= values other than 404 are ignored - a hit still serves the image, a
        // miss below still 404s (no identicon is ever generated server-side).
        var withDefault = await anonymous.GetAsync($"/avatar/{md5}?d=identicon&r=g");
        Assert.Equal(HttpStatusCode.OK, withDefault.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/avatar/{new string('b', 32)}?d=identicon")).StatusCode);

        // s= is honored: a requested 64 comes back as an actual 64×64 PNG, with a
        // size-qualified strong ETag distinct from the canonical one.
        var canonical = await anonymous.GetAsync($"/avatar/{md5}");
        var resized = await anonymous.GetAsync($"/avatar/{md5}?s=64");
        Assert.Equal(HttpStatusCode.OK, resized.StatusCode);
        var resizedBytes = await resized.Content.ReadAsByteArrayAsync();
        Assert.True(PngHeader.TryReadDimensions(resizedBytes, out var width, out var height));
        Assert.Equal((64, 64), (width, height));
        Assert.NotEqual(canonical.Headers.ETag, resized.Headers.ETag);

        // Clamping: absurd sizes land on the documented bounds - never an upscale
        // past the canonical 512, never sub-16 mush.
        var huge = await anonymous.GetAsync($"/avatar/{md5}?s=99999");
        Assert.True(PngHeader.IsAvatarPng(await huge.Content.ReadAsByteArrayAsync()));
        var tiny = await anonymous.GetAsync($"/avatar/{md5}?s=1");
        Assert.True(PngHeader.TryReadDimensions(await tiny.Content.ReadAsByteArrayAsync(), out var tinyWidth, out _));
        Assert.Equal(16, tinyWidth);

        // Unparseable s= means the canonical 512.
        var junk = await anonymous.GetAsync($"/avatar/{md5}?s=large");
        Assert.True(PngHeader.IsAvatarPng(await junk.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task AnonymousRequests_LeaveNoAuditRows_AndProvisionNoUser()
    {
        var (_, md5, _) = await SeedAvatarUserAsync($"anon-{Guid.NewGuid():N}@example.test");
        using var host = EnabledHost();

        long auditBefore, usersBefore;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            auditBefore = await db.AuditEvents.CountAsync();
            usersBefore = await db.Users.CountAsync();
        }

        var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/avatar/{md5}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{new string('c', 32)}")).StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            // No acting user exists on this path, so nothing may be audited (§7 has
            // no anonymous vocabulary) and no JIT row may appear (§11 provisions
            // authenticated requests only). Zero deltas, not "small" deltas.
            Assert.Equal(auditBefore, await db.AuditEvents.CountAsync());
            Assert.Equal(usersBefore, await db.Users.CountAsync());
        }
    }

    [Fact]
    public async Task Gravatar_WithMatchingIfNoneMatch_Returns304()
    {
        var (_, md5, _) = await SeedAvatarUserAsync($"etag-{Guid.NewGuid():N}@example.test");
        using var host = EnabledHost();
        var anonymous = host.CreateClient();

        var first = await anonymous.GetAsync($"/avatar/{md5}");
        Assert.NotNull(first.Headers.ETag);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/avatar/{md5}");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await anonymous.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task EmailDrift_JitRefreshRehashes_OldHash404s_NewHashServes()
    {
        var oldEmail = $"old-{Guid.NewGuid():N}@example.test";
        var newEmail = $"new-{Guid.NewGuid():N}@example.test";
        var (sub, oldMd5, oldSha256) = await SeedAvatarUserAsync(oldEmail);

        using var host = EnabledHost();
        var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/avatar/{oldMd5}")).StatusCode);

        // The user's email changes in Keycloak; their next authenticated request
        // refreshes the mirror (design.md §11 step 3) - same sub, new email claim.
        var refreshed = factory.CreateClient();
        refreshed.SetTestUser(sub: sub, email: newEmail, name: "Gravatar User");
        await refreshed.PostGraphQLAsync("{ me { localUserId } }");

        // A stale hash would now serve the OLD address's avatar to whoever holds
        // that address next - the rehash makes the old hashes dead and the new ones
        // live, atomically with the mirror update.
        var (newMd5, newSha256) = AvatarEmailHasher.Compute(newEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{oldMd5}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{oldSha256}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/avatar/{newMd5}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/avatar/{newSha256}")).StatusCode);
    }

    [Fact]
    public async Task UserWithNoEmail_HasNoHashes_AvatarUnreachableByHash_ButServedInWiki()
    {
        // JIT a user with no email claim at all, then upload.
        var sub = $"grav-noemail-{Guid.NewGuid():N}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub); // no email
        var upload = await client.PostAsync("/avatars",
            AvatarEndpointTests.BuildUpload(AvatarEndpointTests.MakePng(512, 512), "a.png", "image/png"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var userId = await db.Users.Where(u => u.Subject == sub).Select(u => u.Id).SingleAsync();
        var avatar = await db.UserAvatars.AsNoTracking().SingleAsync(a => a.UserId == userId);
        Assert.Null(avatar.EmailHashMd5);
        Assert.Null(avatar.EmailHashSha256);

        // In-wiki rendering doesn't involve hashes and still works.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/users/{userId}/avatar")).StatusCode);
    }
}
