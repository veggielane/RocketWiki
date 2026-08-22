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
/// design.md §6.5.1/§6.5.2: space creation is atomic with its first grant (no
/// default — a required input), and rule management accepts instance-admin OR that
/// space's own space-admin, with the instance-admin arm existing specifically as the
/// bootstrap/recovery path for a space that has reached zero grants. Both landed in
/// Core mid-session; these tests exercise the resolver side wired against them.
/// </summary>
public sealed class SpaceAndAccessRuleMutationTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private static HttpClient AdminClient(RocketWikiApiFactory factory)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);
        return client;
    }

    private static HttpClient PlainClient(RocketWikiApiFactory factory)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"user-{Guid.NewGuid()}");
        return client;
    }

    [Fact]
    public async Task CreateSpace_AsInstanceAdmin_WithInitialGrant_Succeeds()
    {
        var client = AdminClient(factory);
        var key = $"SP{Guid.NewGuid():N}"[..8];

        var result = await client.PostGraphQLAsync($$"""
            mutation {
              createSpace(
                input: { key: "{{key}}", name: "Test Space", description: null }
                initialGrant: { role: SPACE_ADMIN, expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new EveryoneCondition()))}} }
              ) { space { id key } error { kind } }
            }
            """);

        var data = result.RootElement.GetProperty("data").GetProperty("createSpace");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("error").ValueKind);
        Assert.Equal(key, data.GetProperty("space").GetProperty("key").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var spaceId = data.GetProperty("space").GetProperty("id").GetGuid();
        var grantExists = await db.AccessRules.AnyAsync(r =>
            r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == spaceId && r.Role == SpaceRole.SpaceAdmin);
        Assert.True(grantExists, "createSpace must commit the initial grant atomically with the space row.");
    }

    [Fact]
    public async Task CreateSpace_WithoutInstanceAdmin_ReturnsForbidden_AndPersistsNothing()
    {
        var client = PlainClient(factory);
        var key = $"SP{Guid.NewGuid():N}"[..8];

        var result = await client.PostGraphQLAsync($$"""
            mutation {
              createSpace(
                input: { key: "{{key}}", name: "Nope", description: null }
                initialGrant: { role: VIEWER, expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new EveryoneCondition()))}} }
              ) { space { id } error { kind } }
            }
            """);

        var data = result.RootElement.GetProperty("data").GetProperty("createSpace");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("space").ValueKind);
        Assert.Equal("Forbidden", data.GetProperty("error").GetProperty("kind").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.Spaces.AnyAsync(s => s.Key == key));
    }

    [Fact]
    public async Task ArchiveSpace_ThenRestoreSpace_RoundTrips()
    {
        var adminClient = AdminClient(factory);
        var key = $"AR{Guid.NewGuid():N}"[..8];
        var createResult = await adminClient.PostGraphQLAsync($$"""
            mutation {
              createSpace(
                input: { key: "{{key}}", name: "Archive Me", description: null }
                initialGrant: { role: SPACE_ADMIN, expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new EveryoneCondition()))}} }
              ) { space { id } error { kind } }
            }
            """);
        var spaceId = createResult.RootElement.GetProperty("data").GetProperty("createSpace")
            .GetProperty("space").GetProperty("id").GetGuid();

        var archiveResult = await adminClient.PostGraphQLAsync($$"""
            mutation { archiveSpace(input: { spaceId: "{{spaceId}}" }) { space { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null, archiveResult.RootElement.GetProperty("data").GetProperty("archiveSpace").GetProperty("error").ValueKind);

        // Archived - absent from Query.spaces (excluded via the normal soft-delete query filter).
        var listAfterArchive = await adminClient.PostGraphQLAsync("{ spaces { key } }");
        var keysAfterArchive = listAfterArchive.RootElement.GetProperty("data").GetProperty("spaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();
        Assert.DoesNotContain(key, keysAfterArchive);

        var restoreResult = await adminClient.PostGraphQLAsync($$"""
            mutation { restoreSpace(input: { spaceId: "{{spaceId}}" }) { space { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null, restoreResult.RootElement.GetProperty("data").GetProperty("restoreSpace").GetProperty("error").ValueKind);

        var listAfterRestore = await adminClient.PostGraphQLAsync("{ spaces { key } }");
        var keysAfterRestore = listAfterRestore.RootElement.GetProperty("data").GetProperty("spaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();
        Assert.Contains(key, keysAfterRestore);
    }

    [Fact]
    public async Task CreateAccessRule_AsInstanceAdmin_RecoversASpaceWithZeroGrants()
    {
        // design.md §6.5.2: the instance-admin arm exists specifically so a space that
        // somehow reaches zero grants (every grant deleted, a half-completed import) is
        // not permanently unadministrable. Seed exactly that state directly (bypassing
        // createSpace's own atomic-first-grant guarantee, which is precisely why this
        // recovery path has to exist independently of it).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"ZG{Guid.NewGuid():N}"[..8], Name = "Zero Grants", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        await db.SaveChangesAsync();

        var adminClient = AdminClient(factory);
        var result = await adminClient.PostGraphQLAsync($$"""
            mutation {
              createAccessRule(input: {
                kind: SPACE_GRANT, spaceId: "{{space.Id}}", pageId: null, role: SPACE_ADMIN, action: null,
                expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new EveryoneCondition()))}}
              }) { rule { id } error { kind } }
            }
            """);

        var data = result.RootElement.GetProperty("data").GetProperty("createAccessRule");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("error").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, data.GetProperty("rule").ValueKind);
    }

    [Fact]
    public async Task CreateAccessRule_ByNonAdminNonSpaceAdmin_ReturnsForbidden()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"NA{Guid.NewGuid():N}"[..8], Name = "No Access", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        await db.SaveChangesAsync();

        var client = PlainClient(factory); // a mere Viewer, not space-admin, not instance-admin
        var result = await client.PostGraphQLAsync($$"""
            mutation {
              createAccessRule(input: {
                kind: SPACE_GRANT, spaceId: "{{space.Id}}", pageId: null, role: EDITOR, action: null,
                expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new EveryoneCondition()))}}
              }) { rule { id } error { kind } }
            }
            """);

        var data = result.RootElement.GetProperty("data").GetProperty("createAccessRule");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("rule").ValueKind);
        Assert.Equal("Forbidden", data.GetProperty("error").GetProperty("kind").GetString());
    }
}
