using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The audit viewer's pager needs the filtered total (design.md §8's known-deltas
/// list): additive <c>totalCount</c> on AuditEventsConnection. Exact on purpose,
/// unlike search's capped total (§9.1) — this connection is instance-admin-only and
/// applies no per-row permission filter, so an exact COUNT leaks nothing.
/// </summary>
public sealed class AuditEventsTotalCountTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task AuditEvents_TotalCount_IsFilteredTotal_IndependentOfPageSize()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"TC{Guid.NewGuid():N}"[..8], Name = "Total Count Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var pageA = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "a", Title = "Page A", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        var pageB = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "b", Title = "Page B", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.AddRange(pageA, pageB);
        await db.SaveChangesAsync();

        // Two real page.view events for one known user.
        var viewerSub = $"viewer-{Guid.NewGuid()}";
        var viewer = factory.CreateClient();
        viewer.SetTestUser(sub: viewerSub);
        await viewer.PostGraphQLAsync($$"""{ page(id: "{{pageA.Id}}") { title } }""");
        await viewer.PostGraphQLAsync($$"""{ page(id: "{{pageB.Id}}") { title } }""");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var viewerId = await verifyDb.Users.Where(u => u.Subject == viewerSub).Select(u => u.Id).SingleAsync();

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        // first: 1 slices the page to a single node; totalCount stays the filtered total.
        var result = await admin.PostGraphQLAsync($$"""
            { auditEvents(filter: { userId: "{{viewerId}}" }, first: 1) { totalCount nodes { action } } }
            """);
        var connection = result.RootElement.GetProperty("data").GetProperty("auditEvents");

        Assert.Equal(1, connection.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());
    }
}
