using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §6.7: every read path enforces canView, and a filtered-out page must be
/// indistinguishable from one that doesn't exist - no title, no placeholder, nothing
/// that implies something is there. These tests build a tree with restricted branches
/// at several depths and check the exact shape returned for principals with different
/// attributes, not just "access denied vs allowed" as a boolean.
/// </summary>
public class PageReadServiceTests : SqliteTestBase
{
    private static Principal MakePrincipal(string[]? groups = null, Dictionary<string, string[]>? attributes = null)
    {
        var attrs = attributes?.Select(kv => new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, kv.Value));
        return Principal.Create("user-sub", groups ?? Array.Empty<string>(), attrs);
    }

    private static AccessRule ViewerGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Viewer,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule ViewRestriction(Guid pageId, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = expressionJson,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    // --- GetPageAsync -------------------------------------------------------------------

    [Fact]
    public async Task GetPage_VisiblePage_Returned()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new PageReadService(context);
        var result = await service.GetPageAsync(page.Id, MakePrincipal());

        Assert.NotNull(result);
        Assert.Equal(page.Id, result.Id);
    }

    [Fact]
    public async Task GetPage_RestrictedPage_ReturnsNull_SameAsNonExistent()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var nonExistentId = Guid.NewGuid();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(page.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new PageReadService(context);
        var restrictedResult = await service.GetPageAsync(page.Id, MakePrincipal());
        var nonExistentResult = await service.GetPageAsync(nonExistentId, MakePrincipal());

        Assert.Null(restrictedResult);
        Assert.Null(nonExistentResult);
    }

    [Fact]
    public async Task GetPage_NoSpaceRoleAtAll_ReturnsNull()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        // No AccessRule at all - no grant means no role, means no view, per design.md §6.4.
        context.SaveChanges();

        var service = new PageReadService(context);
        var result = await service.GetPageAsync(page.Id, MakePrincipal());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPage_DifferentPrincipalAttributes_SeeDifferentOutcomes()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(page.Id, """{ "attr": "nationality", "in": ["NZ", "US"] }"""));
        context.SaveChanges();

        var service = new PageReadService(context);
        var nzPrincipal = MakePrincipal(attributes: new() { ["nationality"] = new[] { "NZ" } });
        var frPrincipal = MakePrincipal(attributes: new() { ["nationality"] = new[] { "FR" } });
        var noAttributePrincipal = MakePrincipal();

        Assert.NotNull(await service.GetPageAsync(page.Id, nzPrincipal));
        Assert.Null(await service.GetPageAsync(page.Id, frPrincipal));
        Assert.Null(await service.GetPageAsync(page.Id, noAttributePrincipal));
    }

    // --- GetRevisionHistoryAsync ----------------------------------------------------

    [Fact]
    public async Task GetRevisionHistory_VisiblePage_ReturnsRevisionsNewestFirst()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(TestData.NewRevision(page, actor, 1));
        context.PageRevisions.Add(TestData.NewRevision(page, actor, 2));
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new PageReadService(context);
        var result = await service.GetRevisionHistoryAsync(page.Id, MakePrincipal());

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0].RevisionNumber);
        Assert.Equal(1, result[1].RevisionNumber);
    }

    [Fact]
    public async Task GetRevisionHistory_RestrictedPage_ReturnsNull_NotEmptyList()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(TestData.NewRevision(page, actor, 1));
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(page.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new PageReadService(context);
        var result = await service.GetRevisionHistoryAsync(page.Id, MakePrincipal());

        Assert.Null(result); // not Assert.Empty - a restricted page's history isn't "no history", it's "not your business"
    }

    // --- GetPageTreeAsync: the multi-depth restricted-branch scenario ------------------

    /// <summary>
    /// Tree shape:
    /// <code>
    /// Root
    /// ├── PublicChild
    /// ├── RestrictedBranch          (view restricted: nationality in [NZ, US])
    /// │   └── DeepGrandchild        (no restriction of its own - inherits RestrictedBranch's)
    /// └── AnotherChild
    ///     └── DeeplyRestrictedLeaf  (view restricted: group "legal" - AnotherChild itself is open)
    /// </code>
    /// </summary>
    private sealed record TreeFixture(
        Space Space, Page Root, Page PublicChild, Page RestrictedBranch, Page DeepGrandchild, Page AnotherChild, Page DeeplyRestrictedLeaf);

    private TreeFixture SeedMultiDepthTree(RocketWikiDbContext context)
    {
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var publicChild = TestData.NewPage(space, "public-child", root);
        var restrictedBranch = TestData.NewPage(space, "restricted-branch", root);
        var deepGrandchild = TestData.NewPage(space, "deep-grandchild", restrictedBranch);
        var anotherChild = TestData.NewPage(space, "another-child", root);
        var deeplyRestrictedLeaf = TestData.NewPage(space, "deeply-restricted-leaf", anotherChild);

        context.Spaces.Add(space);
        context.Pages.AddRange(root, publicChild, restrictedBranch, deepGrandchild, anotherChild, deeplyRestrictedLeaf);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(restrictedBranch.Id, """{ "attr": "nationality", "in": ["NZ", "US"] }"""));
        context.AccessRules.Add(ViewRestriction(deeplyRestrictedLeaf.Id, """{ "group": "legal" }"""));
        context.SaveChanges();

        return new TreeFixture(space, root, publicChild, restrictedBranch, deepGrandchild, anotherChild, deeplyRestrictedLeaf);
    }

    private static IEnumerable<Guid> FlattenIds(IReadOnlyList<PageTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.Id;
            foreach (var id in FlattenIds(node.Children))
            {
                yield return id;
            }
        }
    }

    [Fact]
    public async Task GetPageTree_PrincipalWithMatchingNationality_SeesRestrictedBranchButNotLegalLeaf()
    {
        using var context = CreateContext();
        var tree = SeedMultiDepthTree(context);

        var service = new PageReadService(context);
        var principal = MakePrincipal(attributes: new() { ["nationality"] = new[] { "NZ" } });
        var result = await service.GetPageTreeAsync(tree.Space.Id, principal);

        var visibleIds = FlattenIds(result).ToHashSet();

        Assert.Contains(tree.Root.Id, visibleIds);
        Assert.Contains(tree.PublicChild.Id, visibleIds);
        Assert.Contains(tree.RestrictedBranch.Id, visibleIds);
        Assert.Contains(tree.DeepGrandchild.Id, visibleIds); // inherits the passing nationality check
        Assert.Contains(tree.AnotherChild.Id, visibleIds);
        Assert.DoesNotContain(tree.DeeplyRestrictedLeaf.Id, visibleIds); // no "legal" group
    }

    [Fact]
    public async Task GetPageTree_PrincipalWithoutNationalityOrLegalGroup_PrunesWholeRestrictedSubtree()
    {
        using var context = CreateContext();
        var tree = SeedMultiDepthTree(context);

        var service = new PageReadService(context);
        var principal = MakePrincipal(); // no attributes, no groups
        var result = await service.GetPageTreeAsync(tree.Space.Id, principal);

        var visibleIds = FlattenIds(result).ToHashSet();

        Assert.Contains(tree.Root.Id, visibleIds);
        Assert.Contains(tree.PublicChild.Id, visibleIds);
        Assert.Contains(tree.AnotherChild.Id, visibleIds);

        // The whole restricted branch is gone - both the restricted node AND its child,
        // even though DeepGrandchild carries no restriction of its own.
        Assert.DoesNotContain(tree.RestrictedBranch.Id, visibleIds);
        Assert.DoesNotContain(tree.DeepGrandchild.Id, visibleIds);
        Assert.DoesNotContain(tree.DeeplyRestrictedLeaf.Id, visibleIds);
    }

    [Fact]
    public async Task GetPageTree_PrincipalInLegalGroup_SeesDeepLeafButNotNationalityRestrictedBranch()
    {
        using var context = CreateContext();
        var tree = SeedMultiDepthTree(context);

        var service = new PageReadService(context);
        var principal = MakePrincipal(groups: new[] { "legal" });
        var result = await service.GetPageTreeAsync(tree.Space.Id, principal);

        var visibleIds = FlattenIds(result).ToHashSet();

        Assert.Contains(tree.AnotherChild.Id, visibleIds);
        Assert.Contains(tree.DeeplyRestrictedLeaf.Id, visibleIds); // passes its own restriction now
        Assert.DoesNotContain(tree.RestrictedBranch.Id, visibleIds); // unrelated restriction, still fails
        Assert.DoesNotContain(tree.DeepGrandchild.Id, visibleIds);
    }

    [Fact]
    public async Task GetPageTree_PrunedNodes_LeakNoTitleOrOtherData()
    {
        // Not just "the id is missing" - nothing about a pruned page's title, slug, or
        // existence should be reconstructable from the returned shape at all.
        using var context = CreateContext();
        var tree = SeedMultiDepthTree(context);

        var service = new PageReadService(context);
        var principal = MakePrincipal(); // sees neither restricted branch
        var result = await service.GetPageTreeAsync(tree.Space.Id, principal);

        var allTitles = Flatten(result).Select(n => n.Title).ToList();
        Assert.DoesNotContain(tree.RestrictedBranch.Title, allTitles);
        Assert.DoesNotContain(tree.DeepGrandchild.Title, allTitles);
        Assert.DoesNotContain(tree.DeeplyRestrictedLeaf.Title, allTitles);

        static IEnumerable<PageTreeNode> Flatten(IReadOnlyList<PageTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var descendant in Flatten(node.Children))
                {
                    yield return descendant;
                }
            }
        }
    }

    [Fact]
    public async Task GetPageTree_NonExistentSpace_ReturnsEmptyList()
    {
        using var context = CreateContext();

        var service = new PageReadService(context);
        var result = await service.GetPageTreeAsync(Guid.NewGuid(), MakePrincipal());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPageTree_PrincipalWithNoSpaceRole_ReturnsEmptyList_EvenThoughUnrestrictedPagesExist()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space); // no restriction on this page at all

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        // No grant at all for this space.
        context.SaveChanges();

        var service = new PageReadService(context);
        var result = await service.GetPageTreeAsync(space.Id, MakePrincipal());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPageTree_PreservesSortOrderAndParentChildShape()
    {
        using var context = CreateContext();
        var tree = SeedMultiDepthTree(context);

        var service = new PageReadService(context);
        var principal = MakePrincipal(attributes: new() { ["nationality"] = new[] { "NZ" } });
        var result = await service.GetPageTreeAsync(tree.Space.Id, principal);

        var root = Assert.Single(result);
        Assert.Equal(tree.Root.Id, root.Id);
        Assert.Equal(3, root.Children.Count); // PublicChild, RestrictedBranch, AnotherChild

        var restrictedBranchNode = root.Children.Single(n => n.Id == tree.RestrictedBranch.Id);
        var deepGrandchildNode = Assert.Single(restrictedBranchNode.Children);
        Assert.Equal(tree.DeepGrandchild.Id, deepGrandchildNode.Id);
    }
}
