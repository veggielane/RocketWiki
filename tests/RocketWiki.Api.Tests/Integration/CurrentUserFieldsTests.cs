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
/// The two server-resolved CurrentUser additions (design.md §8 "known deltas"):
/// isInstanceAdmin (the server's own reading of the token's realm roles, via the
/// same IInstanceRoleAccessor every admin gate uses) and localUserId (the
/// JIT-provisioned mirror row id - the value Comment.authorUserId stores, which
/// me.id, the token subject, can never match). Both are affordance data; the tests
/// pin that they mirror what the server actually enforces/stores rather than being
/// independent claims.
/// </summary>
public sealed class CurrentUserFieldsTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task IsInstanceAdmin_TracksTheRolesClaim_ExactlyAsTheServerGatesDo()
    {
        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"cuf-admin-{Guid.NewGuid()}", roles: ["admin"]);
        var adminResult = await admin.PostGraphQLAsync("{ me { isInstanceAdmin } }");
        Assert.True(adminResult.RootElement.GetProperty("data").GetProperty("me").GetProperty("isInstanceAdmin").GetBoolean());

        var plain = factory.CreateClient();
        plain.SetTestUser(sub: $"cuf-user-{Guid.NewGuid()}", roles: ["user"]);
        var plainResult = await plain.PostGraphQLAsync("{ me { isInstanceAdmin } }");
        Assert.False(plainResult.RootElement.GetProperty("data").GetProperty("me").GetProperty("isInstanceAdmin").GetBoolean());
    }

    [Fact]
    public async Task LocalUserId_IsTheJitProvisionedRowId()
    {
        var sub = $"cuf-jit-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, name: "Local Id User");

        var result = await client.PostGraphQLAsync("{ me { id localUserId } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.Equal(sub, me.GetProperty("id").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var row = await db.Users.SingleAsync(u => u.Subject == sub);
        Assert.Equal(row.Id.ToString(), me.GetProperty("localUserId").GetString(), ignoreCase: true);
        // And the two ids really are different vocabularies - the SPA bug this field
        // exists to fix was comparing me.id (the sub) against author row ids.
        Assert.NotEqual(me.GetProperty("id").GetString(), me.GetProperty("localUserId").GetString());
    }

    [Fact]
    public async Task LocalUserId_MatchesCommentAuthorUserId_RoundTrip()
    {
        // The delete-own-comment affordance end to end: post a comment through the
        // real mutation, then confirm the comment's authorUserId is exactly what
        // me.localUserId reports for the same token.
        var pageId = await SeedCommentablePageAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"cuf-author-{Guid.NewGuid()}", name: "Commenter");

        var added = await client.PostGraphQLAsync($$"""
            mutation {
              addComment(input: { pageId: "{{pageId}}", body: "mine" }) {
                comment { id authorUserId }
                error { kind }
              }
            }
            """);
        var payload = added.RootElement.GetProperty("data").GetProperty("addComment");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        var authorUserId = payload.GetProperty("comment").GetProperty("authorUserId").GetString();

        var me = (await client.PostGraphQLAsync("{ me { localUserId } }"))
            .RootElement.GetProperty("data").GetProperty("me");
        Assert.Equal(authorUserId, me.GetProperty("localUserId").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task Anonymous_GetsFalseAndNull_NeverAProvisionedIdentity()
    {
        var client = factory.CreateClient(); // no SetTestUser

        var result = await client.PostGraphQLAsync("{ me { isAuthenticated isInstanceAdmin localUserId } }");

        var me = result.RootElement.GetProperty("data").GetProperty("me");
        Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.False(me.GetProperty("isInstanceAdmin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("localUserId").ValueKind);
    }

    private async Task<Guid> SeedCommentablePageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = $"CUF{Guid.NewGuid():N}"[..8],
            Name = "CurrentUser Fields Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        });

        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Commentable", CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }
}
