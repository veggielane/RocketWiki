using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8's known-deltas list: the SPA's watch buttons had no "am I watching
/// this?" read path. Page.viewerIsWatching / Space.viewerIsWatching are
/// viewer-relative by construction (keyed on the request's own acting user, never an
/// argument), so the own-vs-other split below is the §6.7-shaped negative: another
/// user's watch is not merely hidden, it is not addressable at all.
/// </summary>
public sealed class WatchStateReadTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Space Space, Page Page)> SeedViewableSpaceWithPageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"WS{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Watch State Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant, SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "watched", Title = "Watched Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        return (space, page);
    }

    [Fact]
    public async Task Page_ViewerIsWatching_TrueForOwnWatch_FalseForAnotherUser()
    {
        var (_, page) = await SeedViewableSpaceWithPageAsync();

        var watcher = factory.CreateClient();
        watcher.SetTestUser(sub: $"watcher-{Guid.NewGuid()}");

        // Before watching: false, not null/error.
        var before = await watcher.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { viewerIsWatching } }""");
        Assert.False(before.RootElement.GetProperty("data").GetProperty("page").GetProperty("viewerIsWatching").GetBoolean());

        var watchResult = await watcher.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{page.Id}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchPage").GetProperty("error").ValueKind);

        var after = await watcher.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { viewerIsWatching } }""");
        Assert.True(after.RootElement.GetProperty("data").GetProperty("page").GetProperty("viewerIsWatching").GetBoolean());

        // The own-vs-other negative: a different caller sees false for the same page -
        // the field only ever reads the caller's own Watch row.
        var otherUser = factory.CreateClient();
        otherUser.SetTestUser(sub: $"other-{Guid.NewGuid()}");
        var other = await otherUser.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { viewerIsWatching } }""");
        Assert.False(other.RootElement.GetProperty("data").GetProperty("page").GetProperty("viewerIsWatching").GetBoolean());
    }

    [Fact]
    public async Task Space_ViewerIsWatching_TrueForOwnWatch_FalseForAnotherUser()
    {
        var (space, _) = await SeedViewableSpaceWithPageAsync();

        var watcher = factory.CreateClient();
        watcher.SetTestUser(sub: $"watcher-{Guid.NewGuid()}");

        var watchResult = await watcher.PostGraphQLAsync($$"""
            mutation { watchSpace(input: { spaceId: "{{space.Id}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchSpace").GetProperty("error").ValueKind);

        var mine = await watcher.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { viewerIsWatching } }""");
        Assert.True(mine.RootElement.GetProperty("data").GetProperty("space").GetProperty("viewerIsWatching").GetBoolean());

        var otherUser = factory.CreateClient();
        otherUser.SetTestUser(sub: $"other-{Guid.NewGuid()}");
        var theirs = await otherUser.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { viewerIsWatching } }""");
        Assert.False(theirs.RootElement.GetProperty("data").GetProperty("space").GetProperty("viewerIsWatching").GetBoolean());
    }
}
