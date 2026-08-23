using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.6 "permissions shape the UI": Page.canEdit/canComment/canManageAccess
/// resolved for the CURRENT caller, tested adversarially - each field asserted false
/// for every principal that must not have it before being asserted true for the one
/// that must. Fixture:
///
/// <code>
/// Space (viewer: everyone, editor: group "editors", space-admin: group "space-admins")
/// └── PageA (unrestricted)
///     └── PageB (EDIT restriction: group "senior")
/// ReplicaSpace (origin "high-side", editor: everyone)
/// └── PageR
/// </code>
/// </summary>
public sealed class ViewerPermissionFieldsTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(Guid SpaceId, Guid PageAId, Guid PageBId, Guid ReplicaPageId);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = $"VPF{Guid.NewGuid():N}"[..8],
            Name = "Viewer Permission Fields Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        AccessRule Grant(Guid spaceId, SpaceRole role, RuleNode expression) => new()
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = spaceId,
            Role = role,
            ExpressionJson = RuleExpressionSerializer.Serialize(expression),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        };

        db.AccessRules.Add(Grant(space.Id, SpaceRole.Viewer, new EveryoneCondition()));
        db.AccessRules.Add(Grant(space.Id, SpaceRole.Editor, new GroupCondition("editors")));
        db.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin, new GroupCondition("space-admins")));

        var pageA = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "a", Title = "Page A", CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.Add(pageA);
        await db.SaveChangesAsync();

        var pageB = new Page
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = "b", Title = "Page B (edit-restricted)", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.Add(pageB);
        await db.SaveChangesAsync();

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            PageId = pageB.Id,
            Action = PageAction.Edit,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition("senior")),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        });

        // A replica space (design.md §12): origin is NOT this instance's id
        // ("standalone" in this fixture), so the read-only invariant applies beneath
        // even its most generous grant.
        var replica = new Space
        {
            Key = $"RPL{Guid.NewGuid():N}"[..8],
            Name = "Replica Space",
            OriginInstanceId = "high-side",
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(replica);
        db.AccessRules.Add(Grant(replica.Id, SpaceRole.Editor, new EveryoneCondition()));

        var pageR = new Page { SpaceId = replica.Id, AncestorPath = "/", Slug = "r", Title = "Replica Page", CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.Add(pageR);
        await db.SaveChangesAsync();

        return new Fixture(space.Id, pageA.Id, pageB.Id, pageR.Id);
    }

    private HttpClient Client(string[]? groups = null, string[]? roles = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"vpf-{Guid.NewGuid()}", groups: groups ?? [], roles: roles);
        return client;
    }

    private static JsonElement Page(JsonDocument doc) =>
        doc.RootElement.GetProperty("data").GetProperty("page");

    private static async Task<JsonDocument> QueryFieldsAsync(HttpClient client, Guid pageId) =>
        await client.PostGraphQLAsync(
            $$"""{ page(id: "{{pageId}}") { canEdit canComment canManageAccess } }""");

    [Fact]
    public async Task ViewerOnly_CanCommentButNeitherEditNorManage()
    {
        var f = await SeedAsync();

        var result = await QueryFieldsAsync(Client(), f.PageAId);

        var page = Page(result);
        Assert.False(page.GetProperty("canEdit").GetBoolean());
        Assert.True(page.GetProperty("canComment").GetBoolean(), "design.md §6.4.2: commenting requires canView, not canEdit.");
        Assert.False(page.GetProperty("canManageAccess").GetBoolean());
    }

    [Fact]
    public async Task Editor_CanEdit_ButStillCannotManageAccess()
    {
        var f = await SeedAsync();

        var result = await QueryFieldsAsync(Client(groups: ["editors"]), f.PageAId);

        var page = Page(result);
        Assert.True(page.GetProperty("canEdit").GetBoolean());
        Assert.False(page.GetProperty("canManageAccess").GetBoolean(),
            "design.md §6.5.2: rule management is space-admin/instance-admin, editor is not enough.");
    }

    [Fact]
    public async Task EditRestriction_TurnsOffCanEdit_ForAnEditorWhoFailsIt_ButNotForOneWhoPassesIt()
    {
        var f = await SeedAsync();

        var failing = Page(await QueryFieldsAsync(Client(groups: ["editors"]), f.PageBId));
        Assert.False(failing.GetProperty("canEdit").GetBoolean());
        Assert.True(failing.GetProperty("canComment").GetBoolean(), "The edit restriction must not bleed into canComment.");

        var passing = Page(await QueryFieldsAsync(Client(groups: ["editors", "senior"]), f.PageBId));
        Assert.True(passing.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task SpaceAdmin_CanManageAccess()
    {
        var f = await SeedAsync();

        var page = Page(await QueryFieldsAsync(Client(groups: ["space-admins"]), f.PageAId));

        Assert.True(page.GetProperty("canManageAccess").GetBoolean());
        Assert.True(page.GetProperty("canEdit").GetBoolean(), "space-admin ⊃ editor (design.md §6.4).");
    }

    [Fact]
    public async Task InstanceAdmin_CanManageAccess_ViaTheRoleArm_WithoutAnySpaceAdminGrant()
    {
        // The §6.5.2 admin arm - but note the admin still had to pass canView (the
        // viewer:everyone grant) to resolve the Page at all: canManageAccess never
        // becomes a read path onto pages the admin cannot see (§6.5 no-read-around).
        var f = await SeedAsync();

        var page = Page(await QueryFieldsAsync(Client(roles: ["admin"]), f.PageAId));

        Assert.True(page.GetProperty("canManageAccess").GetBoolean());
        Assert.False(page.GetProperty("canEdit").GetBoolean(),
            "Instance admin confers rule management, not editing - no silent capability widening (design.md §6.5).");
    }

    [Fact]
    public async Task ReplicaSpace_CanEditAndCanCommentAreFalse_BeneathEveryGrant()
    {
        // design.md §12/§6.4: the read-only invariant beats the replica's own
        // editor:everyone grant - and comments are mutations too.
        var f = await SeedAsync();

        var page = Page(await QueryFieldsAsync(Client(groups: ["editors", "space-admins", "senior"]), f.ReplicaPageId));

        Assert.False(page.GetProperty("canEdit").GetBoolean());
        Assert.False(page.GetProperty("canComment").GetBoolean());
    }

    [Fact]
    public async Task ChildrenList_ResolvesPerPagePermissions_ThroughTheBatch()
    {
        // The N+1 seam: children { canEdit } exercises PagePermissionFactsDataLoader's
        // batch path (one facts computation for the whole list). Correctness assertion:
        // the edit-restricted child reports false while the parent reports true for
        // the same editor in the same request.
        var f = await SeedAsync();
        var client = Client(groups: ["editors"]);

        var result = await client.PostGraphQLAsync(
            $$"""{ page(id: "{{f.PageAId}}") { canEdit children { id canEdit } } }""");

        var page = Page(result);
        Assert.True(page.GetProperty("canEdit").GetBoolean());
        var child = Assert.Single(page.GetProperty("children").EnumerateArray());
        Assert.Equal(f.PageBId.ToString(), child.GetProperty("id").GetString(), ignoreCase: true);
        Assert.False(child.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task PermissionFields_EmitNoExtraAuditRows_BeyondTheNormalPageView()
    {
        // The audit stance stated on PagePermissionFieldResolvers (design.md §7):
        // browsing is already audited; the derived facts add no second row.
        var f = await SeedAsync();
        var client = Client(groups: ["editors"]);

        await QueryFieldsAsync(client, f.PageAId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var rows = db.AuditEvents.Where(e => e.SubjectId == f.PageAId).ToList();
        var row = Assert.Single(rows);
        Assert.Equal("page.view", row.Action);
        Assert.Equal(AuditOutcome.Success, row.Outcome);
    }
}
