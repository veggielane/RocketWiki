using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8's known-deltas list: attachLabel/detachLabel take label ids while
/// every read path returned names only, so no label-editing round trip was possible.
/// Covers the new id-carrying reads (Query.labelDetails, Page.labelDetails), the full
/// create → read-id → attach → read-back → detach loop, the §6.7-shaped negative,
/// and PageTreeNode.labels resolving with ONE PageLabels query for a whole tree.
/// </summary>
public sealed class LabelIdReadPathTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Space Space, Page Root, Page Child, Page Grandchild)> SeedSpaceWithTreeAsync(SpaceRole everyoneRole)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"LI{Guid.NewGuid():N}"[..8], Name = "Label Id Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = everyoneRole,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });

        var root = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "root", Title = "Root", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(root);
        var child = new Page { SpaceId = space.Id, ParentPageId = root.Id, AncestorPath = $"/{root.Id}/", Slug = "child", Title = "Child", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(child);
        var grandchild = new Page { SpaceId = space.Id, ParentPageId = child.Id, AncestorPath = $"/{root.Id}/{child.Id}/", Slug = "grandchild", Title = "Grandchild", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(grandchild);
        await db.SaveChangesAsync();

        return (space, root, child, grandchild);
    }

    private async Task<Label> SeedLabelAsync(Guid spaceId, string name, params Guid[] pageIds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var label = new Label { SpaceId = spaceId, Name = name };
        db.Labels.Add(label);
        foreach (var pageId in pageIds)
        {
            db.PageLabels.Add(new PageLabel { PageId = pageId, LabelId = label.Id });
        }

        await db.SaveChangesAsync();
        return label;
    }

    [Fact]
    public async Task LabelDetails_ReturnsIdsAndNames_ForViewableSpace()
    {
        var (space, root, _, _) = await SeedSpaceWithTreeAsync(SpaceRole.Viewer);
        var label = await SeedLabelAsync(space.Id, $"rocketry-{Guid.NewGuid():N}"[..16], root.Id);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync($$"""{ labelDetails(spaceKey: "{{space.Key}}") { id spaceId name } }""");
        var entry = result.RootElement.GetProperty("data").GetProperty("labelDetails")
            .EnumerateArray().Single(e => e.GetProperty("name").GetString() == label.Name);

        Assert.Equal(label.Id.ToString(), entry.GetProperty("id").GetString());
        Assert.Equal(space.Id.ToString(), entry.GetProperty("spaceId").GetString());
    }

    [Fact]
    public async Task LabelDetails_EmptyForCallerWithNoRole()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"LN{Guid.NewGuid():N}"[..8], Name = "No Role Labels", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.Labels.Add(new Label { SpaceId = space.Id, Name = $"hidden-{Guid.NewGuid():N}"[..14] });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nobody-{Guid.NewGuid()}");

        // Absent, not forbidden (design.md §6.7): the same empty list an unknown key
        // or a label-less space would produce.
        var result = await client.PostGraphQLAsync($$"""{ labelDetails(spaceKey: "{{space.Key}}") { id } }""");
        Assert.Equal(0, result.RootElement.GetProperty("data").GetProperty("labelDetails").GetArrayLength());
    }

    [Fact]
    public async Task LabelEditing_EndToEnd_CreateReadIdAttachReadBackDetach()
    {
        var (space, root, _, _) = await SeedSpaceWithTreeAsync(SpaceRole.Editor);
        var labelName = $"endtoend-{Guid.NewGuid():N}"[..16];
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"editor-{Guid.NewGuid()}");

        // 1. Create (editor may create labels, design.md §6.4.2).
        var createResult = await client.PostGraphQLAsync($$"""
            mutation { createLabel(input: { spaceId: "{{space.Id}}", name: "{{labelName}}" }) { label { id name } error { kind } } }
            """);
        var createData = createResult.RootElement.GetProperty("data").GetProperty("createLabel");
        Assert.Equal(JsonValueKind.Null, createData.GetProperty("error").ValueKind);

        // 2. Read the id back through the READ path (not the mutation payload) - the
        // round trip the SPA actually performs: labelDetails is where the editor maps
        // names to ids.
        var detailsResult = await client.PostGraphQLAsync($$"""{ labelDetails(spaceKey: "{{space.Key}}") { id name } }""");
        var labelId = detailsResult.RootElement.GetProperty("data").GetProperty("labelDetails")
            .EnumerateArray().Single(e => e.GetProperty("name").GetString() == labelName)
            .GetProperty("id").GetString();

        // 3. Attach by that id.
        var attachResult = await client.PostGraphQLAsync($$"""
            mutation { attachLabel(input: { pageId: "{{root.Id}}", labelId: "{{labelId}}" }) { pageLabel { pageId labelId } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            attachResult.RootElement.GetProperty("data").GetProperty("attachLabel").GetProperty("error").ValueKind);

        // 4. Read back on the page: names (shipped field) and ids (new field) agree.
        var pageResult = await client.PostGraphQLAsync($$"""{ page(id: "{{root.Id}}") { labels labelDetails { id name } } }""");
        var page = pageResult.RootElement.GetProperty("data").GetProperty("page");
        Assert.Contains(labelName, page.GetProperty("labels").EnumerateArray().Select(e => e.GetString()));
        var detail = page.GetProperty("labelDetails").EnumerateArray().Single(e => e.GetProperty("name").GetString() == labelName);
        Assert.Equal(labelId, detail.GetProperty("id").GetString());

        // 5. Detach by id; the page's label reads empty out again.
        var detachResult = await client.PostGraphQLAsync($$"""
            mutation { detachLabel(input: { pageId: "{{root.Id}}", labelId: "{{labelId}}" }) { detachedLabel { pageId labelId } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            detachResult.RootElement.GetProperty("data").GetProperty("detachLabel").GetProperty("error").ValueKind);

        var afterResult = await client.PostGraphQLAsync($$"""{ page(id: "{{root.Id}}") { labels labelDetails { id } } }""");
        var after = afterResult.RootElement.GetProperty("data").GetProperty("page");
        Assert.DoesNotContain(labelName, after.GetProperty("labels").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, after.GetProperty("labelDetails").GetArrayLength());
    }

    [Fact]
    public async Task PageTreeNodes_CarryLabels_ResolvedWithOnePageLabelsQuery()
    {
        var (space, root, _, grandchild) = await SeedSpaceWithTreeAsync(SpaceRole.Viewer);
        var rootLabel = await SeedLabelAsync(space.Id, $"tree-root-{Guid.NewGuid():N}"[..16], root.Id);
        var deepLabel = await SeedLabelAsync(space.Id, $"tree-deep-{Guid.NewGuid():N}"[..16], grandchild.Id);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        using var counter = new EfSelectCommandCounter(factory, text => text.Contains("PageLabels"));
        var result = await client.PostGraphQLAsync($$"""
            { pageTree(spaceId: "{{space.Id}}") { id title labels children { id labels children { id labels } } } }
            """);

        var rootNode = result.RootElement.GetProperty("data").GetProperty("pageTree")
            .EnumerateArray().Single(n => n.GetProperty("id").GetString() == root.Id.ToString());
        Assert.Contains(rootLabel.Name, rootNode.GetProperty("labels").EnumerateArray().Select(e => e.GetString()));

        var childNode = rootNode.GetProperty("children").EnumerateArray().Single();
        Assert.Equal(0, childNode.GetProperty("labels").GetArrayLength());

        var grandchildNode = childNode.GetProperty("children").EnumerateArray().Single();
        Assert.Contains(deepLabel.Name, grandchildNode.GetProperty("labels").EnumerateArray().Select(e => e.GetString()));

        // The no-N+1 assertion itself: three nodes at three depths, ONE PageLabels
        // query (the grouped DataLoader batches the whole visible tree's ids).
        Assert.Single(counter.MatchedCommands);
    }
}
