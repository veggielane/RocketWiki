using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8's `Page.restrictions` (own + inherited, flagged) behind §6.5.2's
/// rule-management gate, tested from both sides: managers (space-admin AND
/// instance-admin, the two arms) see the full detail; everyone else gets an empty
/// list that is byte-identical to a page with no restrictions at all — the §6.7
/// absent-not-forbidden convention, asserted at the HTTP boundary. Fixture:
///
/// <code>
/// Space (viewer: everyone, editor: "editors", space-admin: "space-admins")
/// └── Parent (VIEW restriction: nationality NZ - passable by every test principal)
///     └── Child (EDIT restriction: group "senior")
/// Bare (no restrictions anywhere - the byte-identical control)
/// </code>
/// </summary>
public sealed class PageRestrictionsReadTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(
        Guid SpaceId, Guid ParentId, Guid ChildId, Guid BareId,
        Guid ParentViewRuleId, Guid ChildEditRuleId, string SeederDisplayName);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Rule Author", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = $"PRR{Guid.NewGuid():N}"[..8],
            Name = "Restrictions Read Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        void Grant(SpaceRole role, RuleNode expression) => db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = role,
            ExpressionJson = RuleExpressionSerializer.Serialize(expression),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        });

        Grant(SpaceRole.Viewer, new EveryoneCondition());
        Grant(SpaceRole.Editor, new GroupCondition("editors"));
        Grant(SpaceRole.SpaceAdmin, new GroupCondition("space-admins"));

        var parent = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "parent", Title = "Parent", CreatedAtUtc = now, UpdatedAtUtc = now };
        var bare = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "bare", Title = "Bare", CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.AddRange(parent, bare);
        await db.SaveChangesAsync();

        var child = new Page
        {
            SpaceId = space.Id, ParentPageId = parent.Id, AncestorPath = $"/{parent.Id}/",
            Slug = "child", Title = "Child", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.Add(child);
        await db.SaveChangesAsync();

        AccessRule Restriction(Guid pageId, PageAction action, RuleNode expression)
        {
            var rule = new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction,
                PageId = pageId,
                Action = action,
                ExpressionJson = RuleExpressionSerializer.Serialize(expression),
                CreatedAtUtc = now,
                CreatedByUserId = creator.Id,
                UpdatedAtUtc = now,
                UpdatedByUserId = creator.Id,
            };
            db.AccessRules.Add(rule);
            return rule;
        }

        var parentViewRule = Restriction(parent.Id, PageAction.View, new AttrCondition("nationality", ["NZ"]));
        var childEditRule = Restriction(child.Id, PageAction.Edit, new GroupCondition("senior"));
        await db.SaveChangesAsync();

        return new Fixture(space.Id, parent.Id, child.Id, bare.Id, parentViewRule.Id, childEditRule.Id, creator.DisplayName);
    }

    private HttpClient NzClient(string[]? groups = null, string[]? roles = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"prr-{Guid.NewGuid()}", groups: groups ?? [], nationality: ["NZ"], roles: roles);
        return client;
    }

    private const string RestrictionsSelection =
        "{ ruleId pageId pageTitle inherited action expressionJson createdAtUtc updatedAtUtc updatedByDisplayName }";

    [Fact]
    public async Task NonManager_GetsAnEmptyList_ByteIdenticalToAPageWithNoRestrictions()
    {
        // §6.7 discipline applied to the management detail: the response for the
        // restricted Child (which really carries two applicable rules) must be
        // byte-for-byte the response the truly-bare page produces - same body, not
        // merely "both empty-ish". Editors can VIEW both pages; neither arm of the
        // §6.5.2 gate holds for them.
        var f = await SeedAsync();
        var client = NzClient(groups: ["editors"]);

        var restricted = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ page(id: "{{f.ChildId}}") { restrictions {{RestrictionsSelection}} } }""",
        });
        var bare = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ page(id: "{{f.BareId}}") { restrictions {{RestrictionsSelection}} } }""",
        });

        var restrictedBody = await restricted.Content.ReadAsStringAsync();
        Assert.Equal(await bare.Content.ReadAsStringAsync(), restrictedBody);
        // And no rule id or rule content anywhere in the body, belt and braces.
        Assert.DoesNotContain(f.ParentViewRuleId.ToString(), restrictedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.ChildEditRuleId.ToString(), restrictedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("senior", restrictedBody);
    }

    [Fact]
    public async Task SpaceAdmin_SeesOwnAndInheritedRules_Flagged()
    {
        var f = await SeedAsync();
        var client = NzClient(groups: ["space-admins"]);

        var result = await client.PostGraphQLAsync(
            $$"""{ page(id: "{{f.ChildId}}") { restrictions {{RestrictionsSelection}} } }""");

        var rules = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("restrictions")
            .EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);

        // Root-most first (deterministic order): the inherited parent rule, then the
        // child's own.
        var inherited = rules[0];
        Assert.Equal(f.ParentViewRuleId.ToString(), inherited.GetProperty("ruleId").GetString(), ignoreCase: true);
        Assert.Equal(f.ParentId.ToString(), inherited.GetProperty("pageId").GetString(), ignoreCase: true);
        Assert.Equal("Parent", inherited.GetProperty("pageTitle").GetString());
        Assert.True(inherited.GetProperty("inherited").GetBoolean());
        Assert.Equal("VIEW", inherited.GetProperty("action").GetString());
        Assert.Contains("nationality", inherited.GetProperty("expressionJson").GetString());

        var own = rules[1];
        Assert.Equal(f.ChildEditRuleId.ToString(), own.GetProperty("ruleId").GetString(), ignoreCase: true);
        Assert.False(own.GetProperty("inherited").GetBoolean());
        Assert.Equal("EDIT", own.GetProperty("action").GetString());
        Assert.Equal(f.SeederDisplayName, own.GetProperty("updatedByDisplayName").GetString());
    }

    [Fact]
    public async Task InstanceAdmin_SeesTheSameRules_ViaTheRecoveryArm()
    {
        // §6.5.2's second arm. Note the admin still reached the Page through canView
        // (viewer:everyone + passing the NZ view restriction) - the gate widens rule
        // READING for pages the admin can see, never page visibility itself.
        var f = await SeedAsync();
        var client = NzClient(roles: ["admin"]);

        var result = await client.PostGraphQLAsync(
            $$"""{ page(id: "{{f.ChildId}}") { restrictions { ruleId inherited } } }""");

        var rules = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("restrictions")
            .EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);
    }

    [Fact]
    public async Task Viewer_WithNoRoleBeyondEveryone_AlsoGetsTheEmptyList()
    {
        // The weakest non-manager, distinct from the editor case: gate result must
        // not differ by which insufficient role you hold.
        var f = await SeedAsync();
        var client = NzClient();

        var result = await client.PostGraphQLAsync(
            $$"""{ page(id: "{{f.ChildId}}") { restrictions { ruleId } } }""");

        Assert.Empty(result.RootElement.GetProperty("data").GetProperty("page").GetProperty("restrictions").EnumerateArray());
    }
}
