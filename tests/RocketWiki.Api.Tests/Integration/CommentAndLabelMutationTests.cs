using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.4: comment requires canView (not canEdit - a viewer, not just an
/// editor, may comment); label create requires canEdit. Neither carries its own
/// independent restriction beyond the parent Page's (already adversarially proven
/// in PageAdversarialLeakTests), so coverage here is functional rather than
/// adversarial: does the mutation work, and does it correctly refuse when the
/// underlying permission genuinely isn't met.
/// </summary>
public sealed class CommentAndLabelMutationTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Guid SpaceId, Guid PageId)> SeedViewableEditablePageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"CML{Guid.NewGuid():N}"[..8], Name = "Comment/Label Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = creator.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = creator.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = creator.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        return (space.Id, page.Id);
    }

    private static HttpClient AuthedClient(RocketWikiApiFactory factory)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"user-{Guid.NewGuid()}");
        return client;
    }

    [Fact]
    public async Task AddComment_ThenEdit_ThenDelete_AllSucceed()
    {
        var (_, pageId) = await SeedViewableEditablePageAsync();
        var client = AuthedClient(factory);

        var addResult = await client.PostGraphQLAsync($$"""
            mutation { addComment(input: { pageId: "{{pageId}}", body: "First!" }) { comment { id body } error { kind } } }
            """);
        var addData = addResult.RootElement.GetProperty("data").GetProperty("addComment");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, addData.GetProperty("error").ValueKind);
        var commentId = addData.GetProperty("comment").GetProperty("id").GetString();

        var editResult = await client.PostGraphQLAsync($$"""
            mutation { editComment(input: { commentId: "{{commentId}}", body: "Edited!" }) { comment { body } error { kind } } }
            """);
        var editData = editResult.RootElement.GetProperty("data").GetProperty("editComment");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, editData.GetProperty("error").ValueKind);
        Assert.Equal("Edited!", editData.GetProperty("comment").GetProperty("body").GetString());

        var deleteResult = await client.PostGraphQLAsync($$"""
            mutation { deleteComment(input: { commentId: "{{commentId}}" }) { comment { id } error { kind } } }
            """);
        var deleteData = deleteResult.RootElement.GetProperty("data").GetProperty("deleteComment");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, deleteData.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task AddComment_OnPageWithNoSpaceRole_ReturnsForbidden()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();
        // No SpaceGrant at all - nobody has any role, so nobody can view or comment.
        var space = new Space { Key = $"NOG{Guid.NewGuid():N}"[..8], Name = "No Grants Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = creator.Id };
        db.Spaces.Add(space);
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        var client = AuthedClient(factory);
        var result = await client.PostGraphQLAsync($$"""
            mutation { addComment(input: { pageId: "{{page.Id}}", body: "Hi" }) { comment { id } error { kind } } }
            """);

        var data = result.RootElement.GetProperty("data").GetProperty("addComment");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("comment").ValueKind);
        Assert.Equal("Forbidden", data.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task CreateLabel_AttachToPage_ThenDetach_AllSucceed()
    {
        var (spaceId, pageId) = await SeedViewableEditablePageAsync();
        var client = AuthedClient(factory);

        var createResult = await client.PostGraphQLAsync($$"""
            mutation { createLabel(input: { spaceId: "{{spaceId}}", name: "important" }) { label { id name } error { kind } } }
            """);
        var createData = createResult.RootElement.GetProperty("data").GetProperty("createLabel");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, createData.GetProperty("error").ValueKind);
        var labelId = createData.GetProperty("label").GetProperty("id").GetString();

        var attachResult = await client.PostGraphQLAsync($$"""
            mutation { attachLabel(input: { pageId: "{{pageId}}", labelId: "{{labelId}}" }) { pageLabel { labelId } error { kind } } }
            """);
        var attachData = attachResult.RootElement.GetProperty("data").GetProperty("attachLabel");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, attachData.GetProperty("error").ValueKind);

        // Verify it shows up on Page.labels.
        var pageResult = await client.PostGraphQLAsync($$"""{ page(id: "{{pageId}}") { labels } }""");
        var labels = pageResult.RootElement.GetProperty("data").GetProperty("page").GetProperty("labels")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("important", labels);

        var detachResult = await client.PostGraphQLAsync($$"""
            mutation { detachLabel(input: { pageId: "{{pageId}}", labelId: "{{labelId}}" }) { detachedLabel { pageId labelId } error { kind } } }
            """);
        var detachData = detachResult.RootElement.GetProperty("data").GetProperty("detachLabel");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detachData.GetProperty("error").ValueKind);
        // The association that was removed, not a bare id: a payload of scalars carries
        // no __typename, so this mutation could invalidate nothing in the SPA's cache
        // and a removed label left a stale chip behind.
        Assert.Equal(labelId, detachData.GetProperty("detachedLabel").GetProperty("labelId").GetString());

        var pageResultAfter = await client.PostGraphQLAsync($$"""{ page(id: "{{pageId}}") { labels } }""");
        var labelsAfter = pageResultAfter.RootElement.GetProperty("data").GetProperty("page").GetProperty("labels")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.DoesNotContain("important", labelsAfter);
    }
}
