using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §6.4 with two grant kinds: an <b>access grant</b> decides who may see a space
/// (and carries the selector values it confers, §21.15); a <b>role grant</b> decides who
/// may edit or administer it, and confers no visibility at all — "roles never supersede
/// access", pinned below. Fixtures pair the two the way a real space does: an access grant
/// beside every role grant.
/// </summary>
public class EffectivePermissionCalculatorTests
{
    private static Principal MakePrincipal(
        string userId = "user-1",
        string[]? groups = null,
        Dictionary<string, string[]>? attributes = null)
    {
        var attrs = attributes?.Select(kv =>
            new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, kv.Value));
        return Principal.Create(userId, groups ?? Array.Empty<string>(), attrs);
    }

    private static AccessRule AccessGrant(string expressionJson, params SelectorValue[] selectors)
    {
        var rule = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = Guid.NewGuid(),
            ExpressionJson = expressionJson,
        };
        foreach (var selector in selectors)
        {
            rule.Selectors.Add(new AccessRuleSelector { AccessRuleId = rule.Id, Category = selector.Category, Value = selector.Value });
        }

        return rule;
    }

    private static AccessRule RoleGrant(SpaceRole role, string expressionJson) => new()
    {
        Kind = AccessRuleKind.RoleGrant,
        SpaceId = Guid.NewGuid(),
        Role = role,
        ExpressionJson = expressionJson,
    };

    private static AccessRule PageRestriction(Guid pageId, PageAction action, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = action,
        ExpressionJson = expressionJson,
    };

    private const string Everyone = """{ "everyone": true }""";
    private const string Engineering = """{ "group": "engineering" }""";

    // The calculator takes its five inputs as one value (PermissionInputs) so no caller
    // can omit the catalog; these wrappers keep the scenarios readable and pin every one
    // of them to the shared test catalog.
    private static PermissionInputs Inputs(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking) =>
        new(spaceGrants.ToList(), restrictions.ToList(), isReplicaSpace, marking, TestCatalogs.Fruit);

    private static EffectivePermission Compute(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking, Principal principal) =>
        EffectivePermissionCalculator.Compute(Inputs(spaceGrants, restrictions, isReplicaSpace, marking), principal);

    private static EffectivePermissionExplanation Explain(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking, Principal principal) =>
        EffectivePermissionCalculator.Explain(Inputs(spaceGrants, restrictions, isReplicaSpace, marking), principal);

    // --- ComputeSpaceAccess -----------------------------------------------------------

    [Fact]
    public void ComputeSpaceAccess_NoGrantsMatch_ReturnsNull()
    {
        var principal = MakePrincipal();

        Assert.Null(EffectivePermissionCalculator.ComputeSpaceAccess([AccessGrant(Engineering)], principal));
        Assert.Null(EffectivePermissionCalculator.ComputeSpaceAccess([], principal));
    }

    [Fact]
    public void ComputeSpaceAccess_OpenSpace_EveryoneGrantsAccess()
    {
        var access = EffectivePermissionCalculator.ComputeSpaceAccess([AccessGrant(Everyone)], MakePrincipal());

        Assert.NotNull(access);
        Assert.Empty(access.GrantedSelectors);
    }

    [Fact]
    public void ComputeSpaceAccess_UnionsSelectorsAcrossMatchingGrants()
    {
        // design.md §21.15: granted selectors are the UNION over every access grant the
        // principal matches; a grant they do not match contributes nothing.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[]
        {
            AccessGrant(Everyone, TestCatalogs.Apple),
            AccessGrant(Engineering, TestCatalogs.North, TestCatalogs.Apple),
            AccessGrant("""{ "group": "nobody" }""", TestCatalogs.Banana),
        };

        var access = EffectivePermissionCalculator.ComputeSpaceAccess(grants, principal);

        Assert.NotNull(access);
        Assert.Equal([TestCatalogs.Apple, TestCatalogs.North], access.GrantedSelectors.OrderBy(s => s, SelectorValue.CanonicalOrder));
        Assert.DoesNotContain(TestCatalogs.Banana, access.GrantedSelectors);
    }

    [Fact]
    public void ComputeSpaceAccess_IgnoresRoleGrantsAndRestrictions()
    {
        // Roles never supersede access (§6.4): a SpaceAdmin role grant the principal
        // matches is not an access grant, and a restriction is not a grant of anything.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[]
        {
            RoleGrant(SpaceRole.SpaceAdmin, Everyone),
            PageRestriction(Guid.NewGuid(), PageAction.View, Everyone),
        };

        Assert.Null(EffectivePermissionCalculator.ComputeSpaceAccess(grants, principal));
    }

    [Fact]
    public void ComputeSpaceAccess_MalformedGrant_IsTreatedAsNonMatching()
    {
        var principal = MakePrincipal(groups: ["engineering"]);

        Assert.Null(EffectivePermissionCalculator.ComputeSpaceAccess(
            [AccessGrant("""{ "bogus": true }""", TestCatalogs.Apple)], principal));
    }

    // --- ComputeSpaceRole -----------------------------------------------------------

    [Fact]
    public void ComputeSpaceRole_NoGrantsMatch_ReturnsNull()
    {
        var principal = MakePrincipal();
        var grants = new[] { RoleGrant(SpaceRole.Editor, Engineering), AccessGrant(Everyone) };

        Assert.Null(EffectivePermissionCalculator.ComputeSpaceRole(grants, principal));
    }

    [Fact]
    public void ComputeSpaceRole_MultipleMatchingGrants_ReturnsHighest()
    {
        // design.md §6.4: multiple grants OR together; your role is the highest you satisfy.
        var principal = MakePrincipal(groups: ["everyone-group", "engineering"]);
        var grants = new[]
        {
            RoleGrant(SpaceRole.Editor, Everyone),
            RoleGrant(SpaceRole.SpaceAdmin, Engineering),
        };

        Assert.Equal(SpaceRole.SpaceAdmin, EffectivePermissionCalculator.ComputeSpaceRole(grants, principal));
    }

    [Fact]
    public void ComputeSpaceRole_IgnoresAccessGrants_ARoleGrantIsTheOnlySourceOfARole()
    {
        var principal = MakePrincipal();

        Assert.Null(EffectivePermissionCalculator.ComputeSpaceRole([AccessGrant(Everyone, TestCatalogs.Apple)], principal));
    }

    [Fact]
    public void ComputeSpaceRole_MalformedGrant_IsTreatedAsNonMatching()
    {
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { RoleGrant(SpaceRole.SpaceAdmin, """{ "bogus": true }""") };

        Assert.Null(EffectivePermissionCalculator.ComputeSpaceRole(grants, principal));
    }

    // --- Compute: baseline behaviour -------------------------------------------------

    [Fact]
    public void Compute_NoSpaceAccess_DeniesViewAndEdit()
    {
        var principal = MakePrincipal();
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("no-space-access", result.ViewDenialReason);
        Assert.Equal("no-space-access", result.EditDenialReason);
    }

    [Fact]
    public void Compute_AccessWithoutRole_CanViewButNotEdit()
    {
        // The read-only state "viewer" used to name: an access grant and no role grant.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Null(result.ViewDenialReason);
        Assert.Equal("insufficient-space-role", result.EditDenialReason);
    }

    [Fact]
    public void Compute_EditorRoleNoRestrictions_CanViewAndEdit()
    {
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.True(result.CanEdit);
    }

    [Fact]
    public void Compute_ARoleGrantForSomebodyElse_DoesNotEditForYou()
    {
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Everyone), RoleGrant(SpaceRole.Editor, """{ "group": "senior-engineering" }""") };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("insufficient-space-role", result.EditDenialReason);
    }

    // --- Restriction accumulation down the ancestor chain ----------------------------

    [Fact]
    public void Compute_RestrictionOnPageItself_BlocksView()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "top-secret" }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal($"restriction:{pageId}:{restrictions[0].Id}", result.ViewDenialReason);
    }

    [Fact]
    public void Compute_RestrictionOnAncestor_BlocksDescendantEvenWhenOwnRestrictionPasses()
    {
        // design.md §6.4: restrictions accumulate down the tree - a restriction on any
        // ancestor blocks the descendant, regardless of the page's own rules.
        var ancestorPageId = Guid.NewGuid();
        var childPageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(ancestorPageId, PageAction.View, """{ "group": "top-secret" }"""),
            PageRestriction(childPageId, PageAction.View, Everyone),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
    }

    [Fact]
    public void Compute_EditRestrictionFails_CanViewButNotEdit()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.Edit, """{ "group": "senior-engineering" }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public void Compute_ViewRestrictionDoesNotAffectUnrelatedAction_EditStillCheckedIndependently()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering", "senior-engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, Engineering),
            PageRestriction(pageId, PageAction.Edit, """{ "group": "senior-engineering" }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.True(result.CanEdit);
    }

    // --- Replica spaces: unconditional read-only -------------------------------------

    [Fact]
    public void Compute_ReplicaSpace_CanViewButCanNeverEdit_EvenWithSpaceAdminGrant()
    {
        // design.md §6.4/§12: canEdit is unconditionally false on a replica, beating
        // every grant - here the principal is even SpaceAdmin.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.SpaceAdmin, Engineering) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: true, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("replica-read-only", result.EditDenialReason);
    }

    [Fact]
    public void Compute_ReplicaSpace_ViewRestrictionsStillApply()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.SpaceAdmin, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "top-secret" }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: true, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
    }

    // --- No admin bypass --------------------------------------------------------------

    [Fact]
    public void Compute_SpaceAdminRole_DoesNotBypassPageRestriction()
    {
        // design.md §6.5: instance/space admins do not bypass page restrictions. There
        // is no admin flag anywhere in EffectivePermissionCalculator for this test to
        // special-case around - a SpaceAdmin grant is just a role like any other.
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(
            groups: ["admins"],
            attributes: new() { ["nationality"] = ["FR"] });
        var grants = new[] { AccessGrant("""{ "group": "admins" }"""), RoleGrant(SpaceRole.SpaceAdmin, """{ "group": "admins" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "attr": "nationality", "in": ["NZ", "US"] }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
    }

    // --- Fail-closed: missing attribute / unknown group / malformed rule -------------

    [Fact]
    public void Compute_MissingAttribute_FailsClosedOnRestriction()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]); // no nationality attribute at all
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "attr": "nationality", "in": ["NZ", "US"] }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
    }

    [Fact]
    public void Compute_UnknownGroupOnThisInstance_FailsClosedOnRestriction()
    {
        // design.md §12: a synced restriction naming a group unknown on this instance
        // (realm mismatch) matches nobody.
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "clearance-level-9" }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
    }

    [Fact]
    public void Compute_MalformedRestriction_DeniesRatherThanThrowingOrDefaultAllowing()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering), RoleGrant(SpaceRole.Editor, Engineering) };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "not": { "group": "engineering" } }"""),
        };

        var result = Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public void Compute_MalformedRoleGrant_ContributesNoRoleRatherThanThrowing()
    {
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[]
        {
            RoleGrant(SpaceRole.SpaceAdmin, """{ "malformed": true }"""),
            AccessGrant(Engineering),
        };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit); // only the valid access grant counted, not the malformed SpaceAdmin one
    }

    // --- Roles never supersede access (§6.4) -----------------------------------------

    [Fact]
    public void Compute_RoleGrantWithoutAccessGrant_DeniesView_RolesNeverSupersedeAccess()
    {
        // The read-only "viewer" state used to be a role; now visibility is an access
        // grant and nothing else. An Editor role grant the principal matches, with no
        // access grant, is a principal who may edit nothing because they may see nothing.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { RoleGrant(SpaceRole.Editor, Engineering) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("no-space-access", result.ViewDenialReason);
        Assert.Equal("no-space-access", result.EditDenialReason);
    }

    [Fact]
    public void Compute_SpaceAdminRoleWithoutAccess_DeniesView()
    {
        // §6.5.2: a Space-admin with no matching access grant manages a space whose pages
        // they cannot read. The highest role in the scheme rescues nothing on the view side.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { RoleGrant(SpaceRole.SpaceAdmin, Everyone), AccessGrant("""{ "group": "not-engineering" }""") };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.Equal("no-space-access", result.ViewDenialReason);
    }

    [Fact]
    public void HasSpaceAccess_IsAccessGrantsOnly_ButIsSpaceVisible_AdmitsAnyRoleToo()
    {
        // Two questions, two answers (§6.4/§6.5.2): "may see content" is an access grant;
        // "is the space listed for them" is an access grant OR any role grant, so a
        // Space-admin without access can still reach the grants they administer.
        var principal = MakePrincipal(groups: ["engineering"]);
        var roleOnly = new[] { RoleGrant(SpaceRole.SpaceAdmin, Engineering) };
        var accessOnly = new[] { AccessGrant(Engineering) };
        var neither = new[] { AccessGrant("""{ "group": "nobody" }"""), RoleGrant(SpaceRole.Editor, """{ "group": "nobody" }""") };

        Assert.False(EffectivePermissionCalculator.HasSpaceAccess(roleOnly, principal));
        Assert.True(EffectivePermissionCalculator.HasSpaceAccess(accessOnly, principal));
        Assert.False(EffectivePermissionCalculator.HasSpaceAccess(neither, principal));

        Assert.True(EffectivePermissionCalculator.IsSpaceVisible(roleOnly, principal));
        Assert.True(EffectivePermissionCalculator.IsSpaceVisible(accessOnly, principal));
        Assert.False(EffectivePermissionCalculator.IsSpaceVisible(neither, principal));
        Assert.False(EffectivePermissionCalculator.IsSpaceVisible([], principal));
    }

    // --- Selector gates inside the ladder (§21.15) -----------------------------------

    private static readonly ProtectiveMarking AppleMarking =
        ProtectiveMarking.Create(ClassificationLevel.Official, null, [TestCatalogs.Apple]);

    [Fact]
    public void Compute_SelectorGrantedAndEligible_AllowsView()
    {
        var principal = MakePrincipal(groups: ["engineering"], attributes: new() { [TestCatalogs.FruitClaim] = ["yes"] });
        var grants = new[] { AccessGrant(Engineering, TestCatalogs.Apple) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, AppleMarking, principal);

        Assert.True(result.CanView);
    }

    [Fact]
    public void Compute_SelectorNotEligible_DeniesWithTheCategoryToken_BeforeTheGrantIsConsulted()
    {
        // The grant carries APPLE, so G would pass; E fails first and is what is named.
        var principal = MakePrincipal(groups: ["engineering"]);
        var grants = new[] { AccessGrant(Engineering, TestCatalogs.Apple) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, AppleMarking, principal);

        Assert.False(result.CanView);
        Assert.Equal("selector:not_eligible:FRUIT", result.ViewDenialReason);
    }

    [Fact]
    public void Compute_SelectorNotGranted_DeniesWithTheCategoryToken()
    {
        var principal = MakePrincipal(groups: ["engineering"], attributes: new() { [TestCatalogs.FruitClaim] = ["yes"] });
        var grants = new[] { AccessGrant(Engineering, TestCatalogs.Banana), RoleGrant(SpaceRole.SpaceAdmin, Engineering) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, AppleMarking, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("selector:not_granted:FRUIT", result.ViewDenialReason);
    }

    [Fact]
    public void Compute_UnknownSelectorCategory_DeniesEveryone_WithTheUnknownToken()
    {
        // §12: a selector configured only on the instance a bundle came from matches nobody
        // here, and says so distinctly from "not eligible".
        var codeword = new SelectorValue("CODEWORD", "ZEBRA");
        var marking = ProtectiveMarking.Create(ClassificationLevel.Official, null, [codeword]);
        var principal = MakePrincipal(groups: ["engineering"], attributes: new() { [TestCatalogs.FruitClaim] = ["yes"] });
        var grants = new[] { AccessGrant(Engineering, codeword) };

        var result = Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, marking, principal);

        Assert.False(result.CanView);
        Assert.Equal("selector:unknown:CODEWORD", result.ViewDenialReason);
    }

    [Fact]
    public void Compute_AnEmptyCatalog_FailsClosedOnEverySelector()
    {
        // SelectorCatalog.Empty is the fail-closed value, not a way to skip the gates.
        var principal = MakePrincipal(groups: ["engineering"], attributes: new() { [TestCatalogs.FruitClaim] = ["yes"] });
        var inputs = new PermissionInputs(
            [AccessGrant(Engineering, TestCatalogs.Apple)], [], false, AppleMarking, SelectorCatalog.Empty);

        var result = EffectivePermissionCalculator.Compute(inputs, principal);

        Assert.False(result.CanView);
        Assert.Equal("selector:unknown:FRUIT", result.ViewDenialReason);
    }

    // --- EvaluateViewGates: the tree's per-node form of the same ladder ----------------

    [Fact]
    public void EvaluateViewGates_ShortCircuitStopsAtTheFirstFailure_FullFormListsEveryGate()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(); // OFFICIAL_SENSITIVE floor, no fruit claim, no groups
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [TestCatalogs.Apple]);
        var restrictions = new[] { PageRestriction(pageId, PageAction.View, Engineering) };

        var stopped = EffectivePermissionCalculator.EvaluateViewGates(
            SpaceAccess.WithoutSelectors, marking, restrictions, TestCatalogs.Fruit, principal, shortCircuit: true);
        var full = EffectivePermissionCalculator.EvaluateViewGates(
            SpaceAccess.WithoutSelectors, marking, restrictions, TestCatalogs.Fruit, principal, shortCircuit: false);

        var only = Assert.Single(stopped);
        Assert.Equal(GateKind.Classification, only.Kind);
        Assert.Equal("classification:secret", only.Reason);

        Assert.Equal(
            [GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.NationalCaveat, GateKind.ViewRestriction],
            full.Select(g => g.Kind));
        Assert.All(full, g => Assert.False(g.Passed));
        // Same first failure either way - the full form is a superset, never a different answer.
        Assert.Equal(only.Reason, full[0].Reason);
    }
}
