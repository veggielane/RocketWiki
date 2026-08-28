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
/// design.md §20 end to end: an instance admin registers a key, an editor sets a value
/// on a page, the value reads back on <c>Page.properties</c>, and removing it clears it.
/// Plus the two facts the GraphQL layer owns rather than the service: the registry gate
/// and the value gate each write their denial audit row (§7 — denials are audited, with
/// their reason), and <c>Page.properties</c> across several pages costs ONE
/// PageProperties query (§8's DataLoader rule).
/// </summary>
public sealed class PagePropertyTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Space Space, Page Root, Page ChildA, Page ChildB)> SeedSpaceAsync(SpaceRole everyoneRole)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"PP{Guid.NewGuid():N}"[..8], Name = "Property Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = everyoneRole,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });

        var root = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "root", Title = "Root", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(root);
        var childA = new Page { SpaceId = space.Id, ParentPageId = root.Id, AncestorPath = $"/{root.Id}/", Slug = "child-a", Title = "Child A", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        var childB = new Page { SpaceId = space.Id, ParentPageId = root.Id, AncestorPath = $"/{root.Id}/", Slug = "child-b", Title = "Child B", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.AddRange(childA, childB);
        await db.SaveChangesAsync();

        return (space, root, childA, childB);
    }

    /// <summary>Seeds a registry key directly, for tests whose subject is the value
    /// path rather than the registry mutation.</summary>
    private async Task<PagePropertyKey> SeedKeyAsync(string name, int sortOrder = 0)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var key = new PagePropertyKey
        {
            Key = name,
            KeyNormalized = PagePropertyKey.Normalize(name),
            SortOrder = sortOrder,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.PagePropertyKeys.Add(key);
        await db.SaveChangesAsync();
        return key;
    }

    private async Task SeedValueAsync(Guid pageId, Guid keyId, string value)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        db.PageProperties.Add(new PageProperty { PageId = pageId, PagePropertyKeyId = keyId, Value = value, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<List<AuditEvent>> AuditRowsAsync(string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents.Where(e => e.Action == action).ToListAsync();
    }

    [Fact]
    public async Task PageProperties_EndToEnd_CreateKeyThenSetThenReadBackThenRemove()
    {
        var (_, root, _, _) = await SeedSpaceAsync(SpaceRole.Editor);
        var keyName = $"Owner-{Guid.NewGuid():N}"[..16];

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        // 1. An instance admin registers the key.
        var createResult = await admin.PostGraphQLAsync($$"""
            mutation { createPagePropertyKey(input: { key: "{{keyName}}", description: "Who owns it" })
              { propertyKey { id key description sortOrder } error { kind } } }
            """);
        var createData = createResult.RootElement.GetProperty("data").GetProperty("createPagePropertyKey");
        Assert.Equal(JsonValueKind.Null, createData.GetProperty("error").ValueKind);
        var keyId = createData.GetProperty("propertyKey").GetProperty("id").GetString();

        // 2. It appears in the registry listing every authenticated user can read.
        var editor = factory.CreateClient();
        editor.SetTestUser(sub: $"editor-{Guid.NewGuid()}");
        var registryResult = await editor.PostGraphQLAsync("{ pagePropertyKeys { id key description sortOrder } }");
        var registryEntry = registryResult.RootElement.GetProperty("data").GetProperty("pagePropertyKeys")
            .EnumerateArray().Single(e => e.GetProperty("key").GetString() == keyName);
        Assert.Equal(keyId, registryEntry.GetProperty("id").GetString());
        Assert.Equal("Who owns it", registryEntry.GetProperty("description").GetString());

        // 3. An editor (not an admin) sets a value on the page.
        var setResult = await editor.PostGraphQLAsync($$"""
            mutation { setPageProperty(input: { pageId: "{{root.Id}}", pagePropertyKeyId: "{{keyId}}", value: "Ada Lovelace" })
              { property { keyId key value sortOrder } error { kind } } }
            """);
        var setData = setResult.RootElement.GetProperty("data").GetProperty("setPageProperty");
        Assert.Equal(JsonValueKind.Null, setData.GetProperty("error").ValueKind);
        Assert.Equal("Ada Lovelace", setData.GetProperty("property").GetProperty("value").GetString());

        // 4. It reads back on the page itself - properties are visible to anyone who can
        // view the page, with no gate of their own.
        var pageResult = await editor.PostGraphQLAsync($$"""{ page(id: "{{root.Id}}") { properties { keyId key value } } }""");
        var property = pageResult.RootElement.GetProperty("data").GetProperty("page")
            .GetProperty("properties").EnumerateArray().Single();
        Assert.Equal(keyName, property.GetProperty("key").GetString());
        Assert.Equal("Ada Lovelace", property.GetProperty("value").GetString());

        // 5. Setting the same key again overwrites rather than duplicating.
        var overwriteResult = await editor.PostGraphQLAsync($$"""
            mutation { setPageProperty(input: { pageId: "{{root.Id}}", pagePropertyKeyId: "{{keyId}}", value: "Grace Hopper" })
              { property { value } error { kind } } }
            """);
        Assert.Equal("Grace Hopper", overwriteResult.RootElement.GetProperty("data").GetProperty("setPageProperty")
            .GetProperty("property").GetProperty("value").GetString());

        // 6. Remove clears it.
        var removeResult = await editor.PostGraphQLAsync($$"""
            mutation { removePageProperty(input: { pageId: "{{root.Id}}", pagePropertyKeyId: "{{keyId}}" })
              { removedPropertyKeyId error { kind } } }
            """);
        var removeData = removeResult.RootElement.GetProperty("data").GetProperty("removePageProperty");
        Assert.Equal(JsonValueKind.Null, removeData.GetProperty("error").ValueKind);
        Assert.Equal(keyId, removeData.GetProperty("removedPropertyKeyId").GetString());

        var afterResult = await editor.PostGraphQLAsync($$"""{ page(id: "{{root.Id}}") { properties { key } } }""");
        Assert.Equal(0, afterResult.RootElement.GetProperty("data").GetProperty("page").GetProperty("properties").GetArrayLength());

        // 7. The key is now unused, so the admin can retire it.
        var deleteResult = await admin.PostGraphQLAsync($$"""
            mutation { deletePagePropertyKey(keyId: "{{keyId}}") { deletedPropertyKeyId error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            deleteResult.RootElement.GetProperty("data").GetProperty("deletePagePropertyKey").GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task DeletePagePropertyKey_WhileInUse_IsRefused_NamingTheCount()
    {
        var (_, root, childA, _) = await SeedSpaceAsync(SpaceRole.Editor);
        var key = await SeedKeyAsync($"InUse-{Guid.NewGuid():N}"[..16]);
        await SeedValueAsync(root.Id, key.Id, "one");
        await SeedValueAsync(childA.Id, key.Id, "two");

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        var result = await admin.PostGraphQLAsync($$"""
            mutation { deletePagePropertyKey(keyId: "{{key.Id}}") { deletedPropertyKeyId error { kind message } } }
            """);
        var data = result.RootElement.GetProperty("data").GetProperty("deletePagePropertyKey");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("deletedPropertyKeyId").ValueKind);
        Assert.Equal("Validation", data.GetProperty("error").GetProperty("kind").GetString());
        Assert.Contains("2", data.GetProperty("error").GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task CreatePagePropertyKey_ByNonAdmin_IsForbidden_AndAudited()
    {
        var keyName = $"Sneaky-{Guid.NewGuid():N}"[..16];
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nonadmin-{Guid.NewGuid()}"); // no "admin" realm role

        var result = await client.PostGraphQLAsync($$"""
            mutation { createPagePropertyKey(input: { key: "{{keyName}}" }) { propertyKey { id } error { kind message } } }
            """);
        var data = result.RootElement.GetProperty("data").GetProperty("createPagePropertyKey");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("propertyKey").ValueKind);
        Assert.Equal("Forbidden", data.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("instance admin required", data.GetProperty("error").GetProperty("message").GetString());

        // §7: the denial is audited, with its reason and the key it was refused for -
        // subject null, exactly like the emoji registry's admin gate.
        var denial = Assert.Single(
            await AuditRowsAsync("property_key.create"),
            e => e.DetailsJson is not null && e.DetailsJson.Contains(keyName));
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Null(denial.SubjectType);
        Assert.Contains("instance admin required", denial.DetailsJson);
    }

    [Fact]
    public async Task SetPageProperty_ByViewerOnly_IsForbidden_AndAuditedAgainstThePage()
    {
        var (_, root, _, _) = await SeedSpaceAsync(SpaceRole.Viewer);
        var key = await SeedKeyAsync($"Viewer-{Guid.NewGuid():N}"[..16]);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync($$"""
            mutation { setPageProperty(input: { pageId: "{{root.Id}}", pagePropertyKeyId: "{{key.Id}}", value: "nope" })
              { property { value } error { kind message } } }
            """);
        var data = result.RootElement.GetProperty("data").GetProperty("setPageProperty");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("property").ValueKind);
        Assert.Equal("Forbidden", data.GetProperty("error").GetProperty("kind").GetString());

        var denial = Assert.Single(await AuditRowsAsync("page.property.set"), e => e.SubjectId == root.Id);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(AuditSubjectType.Page, denial.SubjectType);
        Assert.Contains("canEdit required", denial.DetailsJson);

        // Fail closed: nothing was written.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await db.PageProperties.AnyAsync(p => p.PageId == root.Id));
    }

    [Fact]
    public async Task PageProperties_AcrossSeveralPages_ResolveWithOnePagePropertiesQuery()
    {
        var (_, root, childA, childB) = await SeedSpaceAsync(SpaceRole.Viewer);
        // Two keys with deliberately reversed sort order vs. alphabetical order, so the
        // assertion below proves sortOrder wins rather than accidentally agreeing.
        var second = await SeedKeyAsync($"Alpha-{Guid.NewGuid():N}"[..14], sortOrder: 20);
        var first = await SeedKeyAsync($"Zulu-{Guid.NewGuid():N}"[..14], sortOrder: 10);
        await SeedValueAsync(root.Id, first.Id, "root-first");
        await SeedValueAsync(root.Id, second.Id, "root-second");
        await SeedValueAsync(childA.Id, first.Id, "child-a");
        await SeedValueAsync(childB.Id, second.Id, "child-b");

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        // Ordering first, on its own request: sortOrder wins over the key's own
        // alphabetical order, so the "Zulu" key (sortOrder 10) precedes "Alpha" (20).
        var orderResult = await client.PostGraphQLAsync($$"""{ page(id: "{{root.Id}}") { properties { key value } } }""");
        Assert.Equal(
            ["root-first", "root-second"],
            orderResult.RootElement.GetProperty("data").GetProperty("page").GetProperty("properties")
                .EnumerateArray().Select(p => p.GetProperty("value").GetString()).ToList());

        // Then the batching assertion, over two sibling pages resolving `properties` at
        // the same depth - the exact shape an N+1 would show up in.
        using var counter = new EfSelectCommandCounter(factory, text => text.Contains("PageProperties"));
        var result = await client.PostGraphQLAsync($$"""
            { page(id: "{{root.Id}}") { children { id properties { key value } } } }
            """);

        var children = result.RootElement.GetProperty("data").GetProperty("page")
            .GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(2, children.Count);
        Assert.Contains(children, c => c.GetProperty("properties").EnumerateArray()
            .Any(p => p.GetProperty("value").GetString() == "child-a"));
        Assert.Contains(children, c => c.GetProperty("properties").EnumerateArray()
            .Any(p => p.GetProperty("value").GetString() == "child-b"));

        // The no-N+1 assertion itself: two pages, ONE PageProperties query.
        Assert.Single(counter.MatchedCommands);
    }
}
