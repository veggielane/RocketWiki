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
        var key = $"SP{Guid.NewGuid():N}"[..8].ToUpperInvariant();

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
        var key = $"SP{Guid.NewGuid():N}"[..8].ToUpperInvariant();

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
        var key = $"AR{Guid.NewGuid():N}"[..8].ToUpperInvariant();
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

    /// <summary>
    /// The whole round trip the SPA's space settings screen needs: set a default page,
    /// read it back through the permission-checked <c>space.homepage</c> resolver, then
    /// clear it with a null <c>pageId</c>. Clearing is the half worth exercising through
    /// the real schema — a nullable input field is what makes it expressible at all, and
    /// a non-null one would have left a space's first homepage permanent.
    /// </summary>
    [Fact]
    public async Task SetSpaceHomepage_SetsIt_ReadsBackThroughTheResolver_AndNullClearsIt()
    {
        var adminClient = AdminClient(factory);
        var (spaceId, spaceKey) = await CreateSpaceAsync(adminClient, "HP");
        var pageId = await CreatePageAsync(adminClient, spaceId, "welcome", "Welcome");

        var set = await adminClient.PostGraphQLAsync($$"""
            mutation { setSpaceHomepage(input: { spaceId: "{{spaceId}}", pageId: "{{pageId}}" }) { space { id } error { kind message } } }
            """);
        Assert.Equal(JsonValueKind.Null, set.RootElement.GetProperty("data").GetProperty("setSpaceHomepage").GetProperty("error").ValueKind);

        var afterSet = await adminClient.PostGraphQLAsync($$"""
            { space(key: "{{spaceKey}}") { homepageId homepage { id title } } }
            """);
        var space = afterSet.RootElement.GetProperty("data").GetProperty("space");
        Assert.Equal(pageId, space.GetProperty("homepageId").GetGuid());
        Assert.Equal("Welcome", space.GetProperty("homepage").GetProperty("title").GetString());

        var cleared = await adminClient.PostGraphQLAsync($$"""
            mutation { setSpaceHomepage(input: { spaceId: "{{spaceId}}", pageId: null }) { space { id } error { kind message } } }
            """);
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("data").GetProperty("setSpaceHomepage").GetProperty("error").ValueKind);

        var afterClear = await adminClient.PostGraphQLAsync($$"""
            { space(key: "{{spaceKey}}") { homepageId homepage { id } } }
            """);
        var clearedSpace = afterClear.RootElement.GetProperty("data").GetProperty("space");
        Assert.Equal(JsonValueKind.Null, clearedSpace.GetProperty("homepageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedSpace.GetProperty("homepage").ValueKind);
    }

    [Fact]
    public async Task SetSpaceHomepage_PageFromAnotherSpace_ReturnsValidationError_AndLeavesTheHomepageUnset()
    {
        var adminClient = AdminClient(factory);
        var (spaceId, spaceKey) = await CreateSpaceAsync(adminClient, "HX");
        var (otherSpaceId, _) = await CreateSpaceAsync(adminClient, "HY");
        var foreignPageId = await CreatePageAsync(adminClient, otherSpaceId, "elsewhere", "Elsewhere");

        var result = await adminClient.PostGraphQLAsync($$"""
            mutation { setSpaceHomepage(input: { spaceId: "{{spaceId}}", pageId: "{{foreignPageId}}" }) { space { id } error { kind message } } }
            """);

        var error = result.RootElement.GetProperty("data").GetProperty("setSpaceHomepage").GetProperty("error");
        Assert.Equal("Validation", error.GetProperty("kind").GetString());
        Assert.Contains($"is not a page in space '{spaceKey}'", error.GetProperty("message").GetString());

        var after = await adminClient.PostGraphQLAsync($$"""{ space(key: "{{spaceKey}}") { homepageId } }""");
        Assert.Equal(
            JsonValueKind.Null,
            after.RootElement.GetProperty("data").GetProperty("space").GetProperty("homepageId").ValueKind);
    }

    [Fact]
    public async Task SetSpaceHomepage_ByNonAdmin_ReturnsForbidden()
    {
        var adminClient = AdminClient(factory);
        // EDITOR, not the helper's default SPACE_ADMIN: the plain caller has to be able to
        // see the space and still be refused, which an everyone-space-admin grant would
        // make impossible. Editor rather than Viewer because the setup below creates a
        // page, and page creation is gated on canEdit - which an instance admin does not
        // bypass (design.md §6.5).
        var (spaceId, spaceKey) = await CreateSpaceAsync(adminClient, "HF", role: "EDITOR");
        var pageId = await CreatePageAsync(adminClient, spaceId, "welcome", "Welcome");

        var result = await PlainClient(factory).PostGraphQLAsync($$"""
            mutation { setSpaceHomepage(input: { spaceId: "{{spaceId}}", pageId: "{{pageId}}" }) { space { id } error { kind } } }
            """);

        Assert.Equal(
            "Forbidden",
            result.RootElement.GetProperty("data").GetProperty("setSpaceHomepage").GetProperty("error").GetProperty("kind").GetString());

        var after = await adminClient.PostGraphQLAsync($$"""{ space(key: "{{spaceKey}}") { homepageId } }""");
        Assert.Equal(
            JsonValueKind.Null,
            after.RootElement.GetProperty("data").GetProperty("space").GetProperty("homepageId").ValueKind);
    }

    /// <summary>
    /// <paramref name="role"/> is the role the everyone-grant confers. It matters for the
    /// forbidden case: the default SPACE_ADMIN grant makes every caller an admin of the
    /// space, so a "plain" client would legitimately be allowed to set the homepage.
    /// </summary>
    private static async Task<(Guid Id, string Key)> CreateSpaceAsync(
        HttpClient adminClient, string prefix, string role = "SPACE_ADMIN")
    {
        // Canonical (upper) because that is what the server stores and echoes back —
        // space keys are canonicalized on write so URLs can be case-insensitive. Sent in
        // canonical form so this helper's returned key is the one the space actually has,
        // rather than the casing a caller happened to type.
        var key = $"{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var result = await adminClient.PostGraphQLAsync($$"""
            mutation {
              createSpace(
                input: { key: "{{key}}", name: "Homepage Space", description: null }
                initialGrant: { role: {{role}}, expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new EveryoneCondition()))}} }
              ) { space { id } error { kind } }
            }
            """);
        var payload = result.RootElement.GetProperty("data").GetProperty("createSpace");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        return (payload.GetProperty("space").GetProperty("id").GetGuid(), key);
    }

    private static async Task<Guid> CreatePageAsync(HttpClient adminClient, Guid spaceId, string slug, string title)
    {
        var result = await adminClient.PostGraphQLAsync($$"""
            mutation {
              createPage(input: { spaceId: "{{spaceId}}", parentPageId: null, slug: "{{slug}}", title: "{{title}}", content: "# {{title}}" }) {
                page { id } error { kind }
              }
            }
            """);
        var payload = result.RootElement.GetProperty("data").GetProperty("createPage");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        return payload.GetProperty("page").GetProperty("id").GetGuid();
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
        var space = new Space { Key = $"ZG{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Zero Grants", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
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
        var space = new Space { Key = $"NA{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "No Access", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
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
