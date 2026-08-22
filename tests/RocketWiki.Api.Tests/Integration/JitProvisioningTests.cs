using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §11 step 3: JIT provisioning of the local <c>User</c> row from token
/// claims, on every authenticated request. Exercised through real HTTP requests
/// against the running pipeline (JitUserProvisioningMiddleware), then verified by
/// reading the SQLite-backed DbContext directly in a fresh scope — the request's
/// own scope is gone by the time the test gets control back.
/// </summary>
public sealed class JitProvisioningTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task AuthenticatedRequest_CreatesLocalUserRow_WithMirroredAttributes()
    {
        var subject = $"jit-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: subject,
            email: "jit.test@example.test",
            name: "JIT Test User",
            groups: ["engineering"],
            nationality: ["NZ", "GB"]);

        await client.PostGraphQLAsync("{ me { id } }");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == subject);

        Assert.Equal("jit.test@example.test", user.Email);
        Assert.Equal("JIT Test User", user.DisplayName);
        Assert.False(user.IsExternal);

        var attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(user.AttributesJson)!;
        Assert.Equal(["NZ", "GB"], attributes["nationality"]);
    }

    [Fact]
    public async Task SameSubjectTwice_UpsertsSameUserRow_DoesNotDuplicate()
    {
        var subject = $"jit-upsert-{Guid.NewGuid()}";
        var client = factory.CreateClient();

        client.SetTestUser(sub: subject, name: "First Name", nationality: ["NZ"]);
        await client.PostGraphQLAsync("{ me { id } }");

        client.SetTestUser(sub: subject, name: "Updated Name", nationality: ["US"]);
        await client.PostGraphQLAsync("{ me { id } }");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        Assert.Equal(1, await db.Users.CountAsync(u => u.Subject == subject));
        var user = await db.Users.SingleAsync(u => u.Subject == subject);
        Assert.Equal("Updated Name", user.DisplayName);
    }

    [Fact]
    public async Task AnonymousRequest_ProvisionsNoUserRow()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var countBefore = await db.Users.CountAsync();

        var client = factory.CreateClient();
        await client.PostGraphQLAsync("{ me { isAuthenticated } }");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(countBefore, await verifyDb.Users.CountAsync());
    }
}
