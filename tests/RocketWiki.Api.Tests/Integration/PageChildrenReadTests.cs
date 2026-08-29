using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// <c>Page.children</c>, which used to be derived by walking the whole space tree and
/// locating the parent inside the pruned result. That had two independent problems and
/// one fix (<c>IPageReadService.GetVisibleChildIdsAsync</c> behind
/// <c>VisibleChildIdsByPageIdDataLoader</c>):
///
/// <list type="number">
/// <item><b>Correctness.</b> Markings do not accumulate down the tree (§21.5), so a page
/// under an over-classified ancestor is pruned from the tree with its whole subtree —
/// while the design says that page "stays reachable by id and through search". Deriving
/// children from the tree meant such a page reported no children at all, so
/// <c>page(id:)</c> and <c>pageTree</c> disagreed about the same subtree.</item>
/// <item><b>Cost.</b> The walk materializes every live page in the space and nothing
/// memoized it, so resolving <c>children</c> across a list ran one full-space walk per
/// row — against design.md §8's DataLoader rule for exactly this field.</item>
/// </list>
/// </summary>
public sealed class PageChildrenReadTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(Guid SecretParentId, Guid ChildId, Guid GrandchildId, Guid[] RootIds);

    /// <summary>
    /// <code>
    /// Space (space-admin: everyone)
    /// ├── SecretParent  [SECRET]        - invisible to an OFFICIAL caller
    /// │   └── Child     [OFFICIAL]      - visible, reachable by id
    /// │       └── Grandchild [OFFICIAL] - must still be reachable via Child.children
    /// ├── RootA [OFFICIAL] └── one child
    /// └── RootB [OFFICIAL] └── one child   (two more parents, for the batching assertion)
    /// </code>
    /// </summary>
    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"kids-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = $"KID{Guid.NewGuid():N}"[..8], Name = "Children Space",
            OriginInstanceId = "standalone", CreatedAtUtc = now, CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.SpaceAdmin,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
        });

        Page NewPage(string slug, Page? parent) => new()
        {
            SpaceId = space.Id,
            ParentPageId = parent?.Id,
            AncestorPath = parent is null ? "/" : $"{parent.AncestorPath}{parent.Id}/",
            Slug = slug,
            Title = slug,
            CurrentContent = $"# {slug}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var secretParent = NewPage("secret-parent", null);
        var child = NewPage("child", secretParent);
        var grandchild = NewPage("grandchild", child);
        var rootA = NewPage("root-a", null);
        var rootAChild = NewPage("root-a-child", rootA);
        var rootB = NewPage("root-b", null);
        var rootBChild = NewPage("root-b-child", rootB);

        // One SaveChanges for the whole graph: ids are client-generated, and an
        // intermediate save would let the every-page-is-marked backstop materialize
        // markings that collide with the explicit ones below.
        db.Pages.AddRange(secretParent, child, grandchild, rootA, rootAChild, rootB, rootBChild);
        db.PageMarkings.Add(Marking(secretParent.Id, ClassificationLevel.Secret));
        foreach (var official in new[] { child, grandchild, rootA, rootAChild, rootB, rootBChild })
        {
            db.PageMarkings.Add(Marking(official.Id, ClassificationLevel.Official));
        }

        await db.SaveChangesAsync();

        return new Fixture(secretParent.Id, child.Id, grandchild.Id, [child.Id, rootA.Id, rootB.Id]);
    }

    private static PageMarking Marking(Guid pageId, ClassificationLevel level) =>
        new() { PageId = pageId, Level = level, SetAtUtc = DateTime.UtcNow };

    private HttpClient OfficialClient()
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"kids-{Guid.NewGuid()}"); // no clearance claim -> OFFICIAL (§21.3)
        return client;
    }

    [Fact]
    public async Task AChildOfAnOverClassifiedParent_StillReportsItsOwnChildren()
    {
        var f = await SeedAsync();
        var client = OfficialClient();

        // Premise: the SECRET parent really is invisible, and the tree really does prune
        // the whole subtree - so `children` cannot be answered from the tree.
        using (var parentRead = await client.PostGraphQLAsync($$"""{ page(id: "{{f.SecretParentId}}") { id } }"""))
        {
            Assert.Equal(
                JsonValueKind.Null, parentRead.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        }

        using var result = await client.PostGraphQLAsync($$"""
            { page(id: "{{f.ChildId}}") { id children { id } } }
            """);

        var page = result.RootElement.GetProperty("data").GetProperty("page");
        Assert.Equal(f.ChildId.ToString(), page.GetProperty("id").GetString());
        var onlyChild = Assert.Single(page.GetProperty("children").EnumerateArray());
        Assert.Equal(f.GrandchildId.ToString(), onlyChild.GetProperty("id").GetString());
    }

    /// <summary>
    /// design.md §8: "DataLoaders batch children... no N+1 queries when resolving the
    /// tree or search results." Three parents resolved in one document must produce
    /// exactly ONE child lookup, not one per parent — asserted on the observed SQL rather
    /// than on the loader wiring, because the old implementation was wired through a
    /// DataLoader too (PageByIdDataLoader) and still ran a full-space walk per parent.
    /// </summary>
    [Fact]
    public async Task ResolvingChildrenAcrossSeveralParents_IssuesOneChildLookupForTheWholeBatch()
    {
        var f = await SeedAsync();
        var client = OfficialClient();

        // Warm the JIT-provisioning path so its own queries can't land inside the count.
        using (var _ = await client.PostGraphQLAsync("{ me { localUserId } }"))
        {
        }

        // The child lookup is the only query that FILTERS on ParentPageId — every other
        // Pages read merely selects the column, so the predicate is what identifies it.
        using var counter = new EfSelectCommandCounter(
            factory, text => text.Contains("\"ParentPageId\" IS NOT NULL"));

        using var result = await client.PostGraphQLAsync($$"""
            {
              a: page(id: "{{f.RootIds[0]}}") { children { id } }
              b: page(id: "{{f.RootIds[1]}}") { children { id } }
              c: page(id: "{{f.RootIds[2]}}") { children { id } }
            }
            """);

        Assert.False(result.RootElement.TryGetProperty("errors", out var errors), $"query errored: {errors}");
        foreach (var alias in new[] { "a", "b", "c" })
        {
            Assert.Single(result.RootElement.GetProperty("data").GetProperty(alias)
                .GetProperty("children").EnumerateArray());
        }

        Assert.Single(counter.MatchedCommands);
    }
}
