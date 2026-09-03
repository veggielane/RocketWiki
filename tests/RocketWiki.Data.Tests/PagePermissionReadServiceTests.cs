using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Core.Tests.Access;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §6.6 / §21.8: the inspector and the restriction listing against a real
/// database, and in particular the one surface that NAMES an ancestor — a chain page's
/// title — which is withheld by the full view gate for that page, not by its level alone.
/// Restrictions accumulate down the tree but markings do not (§21.5), so a caller who
/// can read a child may still be denied the parent by its selectors or caveat; the
/// title of such a page must not leak through the rule list.
/// </summary>
public class PagePermissionReadServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";

    private static Principal PrincipalWith(string userId = "caller-sub", string[]? groups = null, bool fruit = false)
    {
        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>
        {
            new("clearance", ["SECRET"]),
        };
        if (fruit)
        {
            attributes.Add(new(TestCatalogs.FruitClaim, ["yes"]));
        }

        return Principal.Create(userId, groups ?? [], attributes);
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

    /// <summary>A parent marked <c>OFFICIAL APPLE</c> carrying a view restriction everyone
    /// passes, and an unmarked (OFFICIAL, no selectors) child beneath it. The child's own
    /// marking is set explicitly so the persistence-seam inheritance does not copy APPLE
    /// onto it — the point is a readable child under an unreadable parent.</summary>
    private sealed record Chain(Space Space, Page Parent, Page Child, AccessRule ParentRule);

    private static Chain SeedChain(RocketWikiDbContext context)
    {
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");
        var child = TestData.NewPage(space, "child", parent);
        var parentRule = ViewRestriction(parent.Id, """{ "everyone": true }""");

        context.Spaces.Add(space);
        context.Pages.AddRange(parent, child);
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.Official).WithSelectors(TestCatalogs.Apple));
        context.PageMarkings.Add(TestData.NewMarking(child, ClassificationLevel.Official));
        context.AccessRules.Add(parentRule);
        context.SaveChanges();
        return new Chain(space, parent, child, parentRule);
    }

    [Fact]
    public async Task GetRestrictions_AManagerWithNoAccessGrant_SeesTheRules_ButNoTitleAtAll()
    {
        // §6.5.2: managing needs no access grant, so the rule list is theirs; the titles of
        // pages they cannot read are not.
        using var context = CreateContext();
        var chain = SeedChain(context);
        context.AccessRules.Add(RoleGrant(chain.Space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PagePermissionReadService(context, LocalInstanceId);
        var rules = await service.GetRestrictionsAsync(chain.Child.Id, PrincipalWith(fruit: true), callerIsInstanceAdmin: false);

        var rule = Assert.Single(rules);
        Assert.Equal(chain.ParentRule.Id, rule.RuleId);
        Assert.Equal(chain.Parent.Id, rule.PageId);
        Assert.True(rule.Inherited);
        Assert.Equal(string.Empty, rule.PageTitle);
    }

    [Fact]
    public async Task GetRestrictions_AChainPageTheCallerFailsASelectorGateFor_ContributesAnEmptyTitle()
    {
        // Access, eligibility, a role - everything except the APPLE grant. The caller can
        // read the child; the parent's title is withheld by the full gate (G fails), where
        // a level-only check would have let it through.
        using var context = CreateContext();
        var chain = SeedChain(context);
        context.AccessRules.AddRange(AccessGrant(chain.Space.Id), RoleGrant(chain.Space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PagePermissionReadService(context, LocalInstanceId);
        var rules = await service.GetRestrictionsAsync(chain.Child.Id, PrincipalWith(fruit: true), callerIsInstanceAdmin: false);

        var rule = Assert.Single(rules);
        Assert.Equal(string.Empty, rule.PageTitle);

        // Grant APPLE and the same caller is shown the title.
        context.AccessRules.Add(AccessGrant(chain.Space.Id, TestCatalogs.Apple));
        context.SaveChanges();
        var shown = Assert.Single(await new PagePermissionReadService(context, LocalInstanceId)
            .GetRestrictionsAsync(chain.Child.Id, PrincipalWith(fruit: true), callerIsInstanceAdmin: false));
        Assert.Equal(chain.Parent.Title, shown.PageTitle);
    }

    [Fact]
    public async Task Explain_WithholdsAnAncestorTitle_ByTheFullViewGate()
    {
        // The inspector path: the caller can view the child (their own canView passes), so
        // the explanation is Found - and the parent's rule is listed with an empty title
        // because the caller is not granted the parent's selector.
        using var context = CreateContext();
        var chain = SeedChain(context);
        context.AccessRules.Add(AccessGrant(chain.Space.Id));
        context.SaveChanges();

        var caller = PrincipalWith(fruit: true);
        var service = new PagePermissionReadService(context, LocalInstanceId);
        var found = Assert.IsType<ReadResult<PagePermissionExplanation>.Found>(await service.ExplainAsync(chain.Child.Id, caller, caller));

        var restriction = Assert.Single(found.Value.ViewRestrictions);
        Assert.Equal(chain.Parent.Id, restriction.PageId);
        Assert.True(restriction.Passed);
        Assert.Equal(string.Empty, restriction.PageTitle);
        Assert.True(found.Value.HasSpaceAccess);
        Assert.True(found.Value.Permission.CanView);
    }

    [Fact]
    public async Task Explain_ListsEveryGateOfTheLadder_ForTheSubject()
    {
        using var context = CreateContext();
        var chain = SeedChain(context);
        context.AccessRules.AddRange(AccessGrant(chain.Space.Id, TestCatalogs.Apple), RoleGrant(chain.Space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var caller = PrincipalWith(fruit: true);
        var subject = PrincipalWith(userId: "subject-sub"); // access, not eligible for FRUIT
        var service = new PagePermissionReadService(context, LocalInstanceId);
        var found = Assert.IsType<ReadResult<PagePermissionExplanation>.Found>(await service.ExplainAsync(chain.Parent.Id, caller, subject));

        var explanation = found.Value;
        Assert.False(explanation.Permission.CanView);
        Assert.Equal("selector:not_eligible:FRUIT", explanation.Permission.ViewDenialReason);
        Assert.True(explanation.HasSpaceAccess);
        Assert.Equal([TestCatalogs.Apple], explanation.GrantedSelectors);
        Assert.Equal(SpaceRole.Editor, explanation.SpaceRole);
        Assert.Equal(
            [GateKind.SpaceAccess, GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.NationalCaveat, GateKind.ViewRestriction],
            explanation.ViewGates.Select(g => g.Kind));
        Assert.Equal([GateKind.ReplicaReadOnly, GateKind.SpaceRole], explanation.EditGates.Select(g => g.Kind));
        Assert.Single(explanation.ViewGates, g => !g.Passed);
    }

    [Fact]
    public async Task GetPermissionFacts_ReportsHasSpaceAccess_SeparatelyFromTheRole()
    {
        // A Space-admin with no access grant: CanView false, role present, access absent.
        // The API renders that difference (a space they manage but cannot read, §6.5.2).
        using var context = CreateContext();
        var chain = SeedChain(context);
        context.AccessRules.Add(RoleGrant(chain.Space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var facts = await new PagePermissionReadService(context, LocalInstanceId)
            .GetPermissionFactsAsync([chain.Child.Id], PrincipalWith());

        var fact = facts[chain.Child.Id];
        Assert.False(fact.Permission.CanView);
        Assert.Equal("no-space-access", fact.Permission.ViewDenialReason);
        Assert.Equal(SpaceRole.SpaceAdmin, fact.SpaceRole);
        Assert.False(fact.HasSpaceAccess);
        Assert.False(fact.CanComment);
    }
}
