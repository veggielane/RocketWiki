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
/// design.md §8's known-deltas list: comment/attachment authors and trashed-page
/// deleters resolved to display names (id + displayName, the UserRef shape) via the
/// local User mirror — display only, per §6.1: nothing here feeds an authorization
/// decision, and the raw User entity (with its admin-only AttributesJson) never
/// leaves the server. Resolution is batched through UserRefByIdDataLoader; the
/// batching test asserts the observed query count, not just the wiring.
/// </summary>
public sealed class DisplayNameResolutionTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<(Space Space, Page Page, User[] Authors)> SeedPageWithCommentAuthorsAsync(int authorCount)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"DN{Guid.NewGuid():N}"[..8], Name = "Display Name Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "commented", Title = "Commented Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);

        var authors = new User[authorCount];
        for (var i = 0; i < authorCount; i++)
        {
            authors[i] = new User { Subject = $"author-{Guid.NewGuid()}", DisplayName = $"Author {i} {Guid.NewGuid():N}"[..24], CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
            db.Users.Add(authors[i]);
            db.Comments.Add(new Comment
            {
                PageId = page.Id, Body = $"Comment {i}", AuthorUserId = authors[i].Id, CreatedAtUtc = DateTime.UtcNow.AddSeconds(i),
            });
        }

        await db.SaveChangesAsync();
        return (space, page, authors);
    }

    [Fact]
    public async Task CommentAuthors_ResolveIdAndDisplayName_InOneBatchedUsersQuery()
    {
        var (_, page, authors) = await SeedPageWithCommentAuthorsAsync(authorCount: 3);
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"reader-{Guid.NewGuid()}");

        using var counter = new EfSelectCommandCounter(factory, text => text.Contains("Users"));
        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { comments { author { id displayName } } } }""");

        var resolved = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("comments")
            .EnumerateArray()
            .Select(c => (Id: c.GetProperty("author").GetProperty("id").GetString(),
                          Name: c.GetProperty("author").GetProperty("displayName").GetString()))
            .ToArray();

        Assert.Equal(3, resolved.Length);
        foreach (var author in authors)
        {
            Assert.Contains(resolved, r => r.Id == author.Id.ToString() && r.Name == author.DisplayName);
        }

        // Exactly two Users SELECTs for the whole request: the JIT-provisioning lookup
        // of the caller, plus ONE UserRefByIdDataLoader batch. Three per-row lookups
        // (the N+1 this loader exists to prevent) would make this four.
        Assert.Equal(2, counter.MatchedCommands.Count);
    }

    [Fact]
    public async Task Attachment_UploadedBy_ResolvesDisplayName()
    {
        var (_, page, authors) = await SeedPageWithCommentAuthorsAsync(authorCount: 1);
        var uploader = authors[0];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            db.Attachments.Add(new Attachment
            {
                PageId = page.Id, FileName = "thrust-curve.csv", ContentType = "text/csv", SizeBytes = 42,
                ContentHash = new byte[32], StorageKey = $"attachments/test/{Guid.NewGuid()}",
                UploadedByUserId = uploader.Id, CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"reader-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { attachments { uploadedBy { id displayName } } } }""");
        var uploadedBy = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("attachments")
            .EnumerateArray().Single().GetProperty("uploadedBy");

        Assert.Equal(uploader.Id.ToString(), uploadedBy.GetProperty("id").GetString());
        Assert.Equal(uploader.DisplayName, uploadedBy.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TrashedPage_DeletedBy_ResolvesDisplayName_AndIsNullForLivePages()
    {
        var (space, livePage, authors) = await SeedPageWithCommentAuthorsAsync(authorCount: 1);
        var deleter = authors[0];
        Guid trashedPageId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var trashed = new Page
            {
                SpaceId = space.Id, AncestorPath = "/", Slug = "trashed", Title = "Trashed Page",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
                IsDeleted = true, DeletedAtUtc = DateTime.UtcNow, DeletedByUserId = deleter.Id, DeleteBatchId = Guid.NewGuid(),
            };
            db.Pages.Add(trashed);
            await db.SaveChangesAsync();
            trashedPageId = trashed.Id;
        }

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"editor-{Guid.NewGuid()}");

        var trashResult = await client.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { trashedPages { id deletedBy { id displayName } } } }""");
        var deletedBy = trashResult.RootElement.GetProperty("data").GetProperty("space").GetProperty("trashedPages")
            .EnumerateArray().Single(p => p.GetProperty("id").GetString() == trashedPageId.ToString())
            .GetProperty("deletedBy");
        Assert.Equal(deleter.Id.ToString(), deletedBy.GetProperty("id").GetString());
        Assert.Equal(deleter.DisplayName, deletedBy.GetProperty("displayName").GetString());

        var liveResult = await client.PostGraphQLAsync($$"""{ page(id: "{{livePage.Id}}") { deletedBy { id } } }""");
        Assert.Equal(JsonValueKind.Null,
            liveResult.RootElement.GetProperty("data").GetProperty("page").GetProperty("deletedBy").ValueKind);
    }

    [Fact]
    public async Task AuditEvent_UserDisplayName_StillResolves_ThroughTheSharedLoader()
    {
        // Regression guard for swapping AuditEventFieldResolvers' per-row query to
        // UserRefByIdDataLoader: same schema field, same value, now batched.
        var (_, page, _) = await SeedPageWithCommentAuthorsAsync(authorCount: 1);
        var viewerName = $"Audit Viewer {Guid.NewGuid():N}"[..20];
        var viewerSub = $"audited-{Guid.NewGuid()}";
        var viewer = factory.CreateClient();
        viewer.SetTestUser(sub: viewerSub, name: viewerName);
        await viewer.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { title } }""");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var viewerId = await db.Users.Where(u => u.Subject == viewerSub).Select(u => u.Id).SingleAsync();

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);
        var result = await admin.PostGraphQLAsync($$"""
            { auditEvents(filter: { userId: "{{viewerId}}" }) { nodes { userDisplayName action } } }
            """);

        var names = result.RootElement.GetProperty("data").GetProperty("auditEvents").GetProperty("nodes")
            .EnumerateArray().Select(n => n.GetProperty("userDisplayName").GetString()).ToArray();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Equal(viewerName, n));
    }
}
