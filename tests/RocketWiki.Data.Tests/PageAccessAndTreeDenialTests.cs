using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Core.Tests.Access;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §6.7 / §21.8 against a real database: the DISCLOSED read
/// (<c>GetPageAccessAsync</c>) and the tree's protected entries. What a denied caller is
/// told is the page's marking and every gate they failed - unless no access grant admits
/// them to the space, in which case they are told exactly that and nothing else. And the
/// tree's per-node verdict is the id fetch's verdict, reason for reason.
/// </summary>
public class PageAccessAndTreeDenialTests : SqliteTestBase
{
    private static Principal PrincipalWith(string? clearance = null, string[]? nationality = null, string[]? groups = null, bool fruit = false)
    {
        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        if (clearance is not null)
        {
            attributes.Add(new("clearance", [clearance]));
        }

        if (nationality is not null)
        {
            attributes.Add(new("nationality", nationality));
        }

        if (fruit)
        {
            attributes.Add(new(TestCatalogs.FruitClaim, ["yes"]));
        }

        return Principal.Create("user-sub", groups ?? [], attributes);
    }

    private static AccessRule AccessGrant(Guid spaceId, params SelectorValue[] selectors)
    {
        var grant = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = spaceId,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        };
        foreach (var selector in selectors)
        {
            grant.Selectors.Add(new AccessRuleSelector { AccessRuleId = grant.Id, Category = selector.Category, Value = selector.Value });
        }

        return grant;
    }

    private static AccessRule RoleGrant(Guid spaceId, SpaceRole role) => new()
    {
        Kind = AccessRuleKind.RoleGrant,
        SpaceId = spaceId,
        Role = role,
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

    // --- GetPageAccessAsync --------------------------------------------------------------

    [Fact]
    public async Task GetPageAccess_DeniedByTheMarking_CarriesTheMarkingAndEveryFailedGate()
    {
        // SECRET APPLE US EYES ONLY behind an engineering-only restriction, read by an
        // OFFICIAL-SENSITIVE (floor) NZ caller who is not eligible, not granted and not in
        // engineering: five failed gates, the level named first, and the marking disclosed
        // so the placeholder can carry its label.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "US").WithSelectors(TestCatalogs.Apple));
        context.AccessRules.Add(AccessGrant(space.Id));
        var restriction = ViewRestriction(page.Id, """{ "group": "engineering" }""");
        context.AccessRules.Add(restriction);
        context.SaveChanges();

        var access = await new PageReadService(context).GetPageAccessAsync(page.Id, PrincipalWith(nationality: ["NZ"]));

        var denial = Assert.IsType<PageAccess.Denied>(access).Denial;
        Assert.Equal("classification:secret", denial.Reason);
        Assert.False(denial.NoSpaceAccess);
        Assert.NotNull(denial.Marking);
        Assert.Equal("UK SECRET APPLE US EYES ONLY", denial.Marking.Format(TestCatalogs.Fruit));
        Assert.Equal(
            [GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.NationalCaveat, GateKind.ViewRestriction],
            denial.Reasons.Select(r => r.Kind));
        Assert.All(denial.Reasons, r => Assert.False(r.Passed));
        Assert.Equal($"restriction:{page.Id}:{restriction.Id}", denial.Reasons[^1].Reason);
    }

    [Fact]
    public async Task GetPageAccess_NoSpaceAccess_WithholdsTheMarking_AndListsOnlyTheSpaceGate()
    {
        // A Space-admin with no access grant: told exactly "you have no access to this
        // space" - not the level, not the selector, not the caveat the inspector would
        // still have evaluated (§21.8).
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.TopSecret, "US").WithSelectors(TestCatalogs.Apple));
        context.AccessRules.Add(RoleGrant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var access = await new PageReadService(context).GetPageAccessAsync(page.Id, PrincipalWith("SECRET", ["UK"]));

        var denial = Assert.IsType<PageAccess.Denied>(access).Denial;
        Assert.Equal("no-space-access", denial.Reason);
        Assert.True(denial.NoSpaceAccess);
        Assert.Null(denial.Marking);
        var only = Assert.Single(denial.Reasons);
        Assert.Equal(GateKind.SpaceAccess, only.Kind);
        Assert.Equal("no-space-access", only.Reason);
    }

    [Fact]
    public async Task GetPageAccess_MissingIsNotFound_AndVisibleIsFound_AgreeingWithGetPage()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(AccessGrant(space.Id));
        context.SaveChanges();

        var service = new PageReadService(context);

        Assert.IsType<PageAccess.NotFound>(await service.GetPageAccessAsync(Guid.NewGuid(), PrincipalWith()));
        var found = Assert.IsType<PageAccess.Found>(await service.GetPageAccessAsync(page.Id, PrincipalWith()));
        Assert.Equal(page.Id, found.Page.Id);
        Assert.IsType<ReadResult<Page>.Found>(await service.GetPageAsync(page.Id, PrincipalWith()));
    }

    [Fact]
    public async Task GetPageAccessBatch_HasAnEntryForEveryRequestedId_FoundDeniedOrNotFound()
    {
        var space = TestData.NewSpace();
        var open = TestData.NewPage(space, "open");
        var secret = TestData.NewPage(space, "secret");
        var missing = Guid.NewGuid();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(open, secret);
        context.PageMarkings.Add(TestData.NewMarking(secret, ClassificationLevel.Secret));
        context.AccessRules.Add(AccessGrant(space.Id));
        context.SaveChanges();

        var batch = await new PageReadService(context).GetPageAccessBatchAsync([open.Id, secret.Id, missing, open.Id], PrincipalWith());

        Assert.Equal(3, batch.Count);
        Assert.IsType<PageAccess.Found>(batch[open.Id]);
        var denial = Assert.IsType<PageAccess.Denied>(batch[secret.Id]).Denial;
        Assert.Equal("classification:secret", denial.Reason);
        Assert.Equal(ClassificationLevel.Secret, denial.Marking!.Level);
        Assert.IsType<PageAccess.NotFound>(batch[missing]);
    }

    // --- The tree's protected entries ----------------------------------------------------

    [Fact]
    public async Task PageTree_ADeniedNode_IsALeafDenialCarryingEveryFailedGate_AndNoTitle()
    {
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var compartment = TestData.NewPage(space, "compartment", root);
        var beneath = TestData.NewPage(space, "beneath", compartment);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(root, compartment, beneath);
        context.PageMarkings.Add(TestData.NewMarking(compartment, ClassificationLevel.Secret, "US").WithSelectors(TestCatalogs.Apple));
        context.AccessRules.Add(AccessGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(compartment.Id, """{ "group": "engineering" }"""));
        context.SaveChanges();

        var tree = Assert.IsType<ReadResult<IReadOnlyList<PageTreeEntry>>.Found>(
            await new PageReadService(context).GetPageTreeAsync(space.Id, PrincipalWith(nationality: ["NZ"]))).Value;

        var rootNode = Assert.IsType<PageTreeNode>(Assert.Single(tree));
        var placeholder = Assert.IsType<ProtectedTreeNode>(Assert.Single(rootNode.Children));

        // Position, denial, marking - and nothing else: no id, title, slug or children
        // exist on the type to leak (§6.7's leak analysis holds by construction).
        Assert.Equal(compartment.SortOrder, placeholder.SortOrder);
        Assert.Equal("classification:secret", placeholder.Denial.Reason);
        Assert.False(placeholder.Denial.NoSpaceAccess);
        Assert.Equal("UK SECRET APPLE US EYES ONLY", placeholder.Denial.Marking!.Format(TestCatalogs.Fruit));
        Assert.Equal(
            [GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.NationalCaveat, GateKind.ViewRestriction],
            placeholder.Denial.Reasons.Select(r => r.Kind));
        Assert.DoesNotContain(compartment.Title, placeholder.ToString());
        Assert.DoesNotContain(beneath.Title, placeholder.ToString());
        Assert.DoesNotContain(compartment.Slug, placeholder.ToString());
    }

    [Fact]
    public async Task PageTree_NodeVerdict_MatchesGetPage_ForEveryPageInTheSpace()
    {
        // Parity between the tree walk and the id fetch, over a space that exercises every
        // gate kind: a SECRET branch, a selector-bearing branch, a restricted branch, a
        // caveated leaf, and open pages beneath each - for two callers who fail different
        // gates. Every entry the walk produces is checked against GetPageAsync for the
        // same page: a visible node must be Found, a protected entry must be Denied with
        // the same reason.
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var secretBranch = TestData.NewPage(space, "secret-branch", root);
        var underSecret = TestData.NewPage(space, "under-secret", secretBranch);
        var appleBranch = TestData.NewPage(space, "apple-branch", root);
        var underApple = TestData.NewPage(space, "under-apple", appleBranch);
        var restrictedBranch = TestData.NewPage(space, "restricted-branch", root);
        var underRestricted = TestData.NewPage(space, "under-restricted", restrictedBranch);
        var caveated = TestData.NewPage(space, "caveated", root);
        var open = TestData.NewPage(space, "open", root);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(root, secretBranch, underSecret, appleBranch, underApple, restrictedBranch, underRestricted, caveated, open);
        context.PageMarkings.Add(TestData.NewMarking(secretBranch, ClassificationLevel.Secret));
        context.PageMarkings.Add(TestData.NewMarking(underSecret, ClassificationLevel.Official));
        context.PageMarkings.Add(TestData.NewMarking(appleBranch, ClassificationLevel.Official).WithSelectors(TestCatalogs.Apple));
        context.PageMarkings.Add(TestData.NewMarking(underApple, ClassificationLevel.Official));
        context.PageMarkings.Add(TestData.NewMarking(caveated, ClassificationLevel.Official, "US"));
        context.AccessRules.Add(AccessGrant(space.Id, TestCatalogs.Apple));
        context.AccessRules.Add(ViewRestriction(restrictedBranch.Id, """{ "group": "engineering" }"""));
        context.SaveChanges();

        var service = new PageReadService(context);
        var callers = new[]
        {
            PrincipalWith("SECRET", ["US"], ["engineering"], fruit: true), // sees everything
            PrincipalWith(nationality: ["NZ"]),                             // fails C, E, R and N
            PrincipalWith("TOP_SECRET", ["US"], fruit: false),              // fails E only
        };

        foreach (var caller in callers)
        {
            var tree = Assert.IsType<ReadResult<IReadOnlyList<PageTreeEntry>>.Found>(
                await service.GetPageTreeAsync(space.Id, caller)).Value;
            var walked = await AssertParityAsync(context, service, caller, parentId: null, tree);
            Assert.True(walked > 0);
        }
    }

    /// <summary>Walks entries beside the database's children of the same parent, in the
    /// service's own sibling order, and returns how many entries were checked.</summary>
    private static async Task<int> AssertParityAsync(
        RocketWikiDbContext context, PageReadService service, Principal caller, Guid? parentId, IReadOnlyList<PageTreeEntry> entries)
    {
        var siblings = await context.Pages.AsNoTracking()
            .Where(p => p.ParentPageId == parentId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .ToListAsync();
        Assert.Equal(siblings.Count, entries.Count);

        var walked = 0;
        for (var i = 0; i < siblings.Count; i++)
        {
            var page = siblings[i];
            var read = await service.GetPageAsync(page.Id, caller);
            walked++;
            switch (entries[i])
            {
                case PageTreeNode node:
                    Assert.Equal(page.Id, node.Id);
                    Assert.IsType<ReadResult<Page>.Found>(read);
                    walked += await AssertParityAsync(context, service, caller, page.Id, node.Children);
                    break;
                case ProtectedTreeNode placeholder:
                    var denied = Assert.IsType<ReadResult<Page>.Denied>(read);
                    Assert.Equal(denied.Reason, placeholder.Denial.Reason);
                    Assert.Equal(page.SortOrder, placeholder.SortOrder);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected entry {entries[i].GetType().Name}.");
            }
        }

        return walked;
    }
}
