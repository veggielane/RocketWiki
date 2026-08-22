using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

public sealed class TrashAndSpacesQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(User Seeder, Space Space, Page Page)> SeedSpaceWithGrantAsync(SpaceRole role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"TR{Guid.NewGuid():N}"[..8], Name = "Trash Test Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = role,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Trashed Page",
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            IsDeleted = true, DeletedAtUtc = DateTime.UtcNow, DeletedByUserId = seeder.Id, DeleteBatchId = Guid.NewGuid(),
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        return (seeder, space, page);
    }

    [Fact]
    public async Task TrashedPages_VisibleToEditor()
    {
        var (_, space, page) = await SeedSpaceWithGrantAsync(SpaceRole.Editor);
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"editor-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { trashedPages { id title } } }""");
        var titles = result.RootElement.GetProperty("data").GetProperty("space").GetProperty("trashedPages")
            .EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToArray();

        Assert.Contains(page.Title, titles);
    }

    [Fact]
    public async Task TrashedPages_AbsentForViewerOnly()
    {
        // Viewer role satisfies canView on the space, but not the Editor+ trash requires
        // (design.md §6.4: canEdit needs Editor+, and restore needs canEdit).
        var (_, space, _) = await SeedSpaceWithGrantAsync(SpaceRole.Viewer);
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { trashedPages { id } } }""");
        var trashedPages = result.RootElement.GetProperty("data").GetProperty("space").GetProperty("trashedPages");

        Assert.Equal(0, trashedPages.GetArrayLength());
    }

    [Fact]
    public async Task Spaces_OnlyReturnsSpacesCallerHasARoleIn()
    {
        var (_, spaceTheyCanSee, _) = await SeedSpaceWithGrantAsync(SpaceRole.Viewer);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var otherSeeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Other Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(otherSeeder);
        await db.SaveChangesAsync();
        var spaceTheyCannotSee = new Space { Key = $"HD{Guid.NewGuid():N}"[..8], Name = "Hidden Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = otherSeeder.Id };
        db.Spaces.Add(spaceTheyCannotSee);
        // No grant at all for this space - nobody has any role in it.
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync("{ spaces { key } }");
        var keys = result.RootElement.GetProperty("data").GetProperty("spaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.Contains(spaceTheyCanSee.Key, keys);
        Assert.DoesNotContain(spaceTheyCannotSee.Key, keys);
    }

    [Fact]
    public async Task Space_ByKey_IsAbsentForCallerWithNoRole()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"NR{Guid.NewGuid():N}"[..8], Name = "No Role Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nobody-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { id } }""");
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("space").ValueKind);
    }

    [Fact]
    public async Task Grants_VisibleToSpaceAdmin_AbsentToViewer()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"GR{Guid.NewGuid():N}"[..8], Name = "Grants Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        // Two grants: everyone is a Viewer, and a specific "boss" group is SpaceAdmin.
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.SpaceAdmin,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition("boss")),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        await db.SaveChangesAsync();

        var adminClient = factory.CreateClient();
        adminClient.SetTestUser(sub: $"boss-{Guid.NewGuid()}", groups: ["boss"]);
        var adminResult = await adminClient.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { grants { id role } } }""");
        Assert.Equal(2, adminResult.RootElement.GetProperty("data").GetProperty("space").GetProperty("grants").GetArrayLength());

        var viewerClient = factory.CreateClient();
        viewerClient.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");
        var viewerResult = await viewerClient.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { grants { id } } }""");
        Assert.Equal(0, viewerResult.RootElement.GetProperty("data").GetProperty("space").GetProperty("grants").GetArrayLength());
    }
}
