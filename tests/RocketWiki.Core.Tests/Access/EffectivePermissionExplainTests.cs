using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §6.6: the inspector's Explain path is "a separate method, never a
/// relaxation of the gate". Two properties are load-bearing and both are pinned
/// here: (1) Explain's verdict — CanView/CanEdit AND the denial-reason strings —
/// is identical to Compute's for the same inputs, across every branch of the
/// ladder (no access, role-only, each marking gate, failing view restriction, replica,
/// access-only, failing edit restriction, full allow, malformed rules); (2) unlike the
/// gate, Explain evaluates EVERY gate and reports each pass/fail rather than stopping
/// at the first failure — the whole reason it exists — including the marking gates of
/// a page whose space the principal cannot even enter.
/// </summary>
public class EffectivePermissionExplainTests
{
    private static Principal MakePrincipal(
        string userId = "user-1",
        string[]? groups = null,
        Dictionary<string, string[]>? attributes = null)
    {
        var attrs = attributes?.Select(kv =>
            new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, kv.Value));
        return Principal.Create(userId, groups ?? [], attrs);
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

    private static PermissionInputs Inputs(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking) =>
        new(spaceGrants.ToList(), restrictions.ToList(), isReplicaSpace, marking, TestCatalogs.Fruit);

    private static EffectivePermission Compute(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking, Principal principal) =>
        EffectivePermissionCalculator.Compute(Inputs(spaceGrants, restrictions, isReplicaSpace, marking), principal);

    private static EffectivePermissionExplanation Explain(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking, Principal principal) =>
        EffectivePermissionCalculator.Explain(Inputs(spaceGrants, restrictions, isReplicaSpace, marking), principal);

    /// <summary>Every distinct branch of the verdict ladder, as (grants, restrictions,
    /// replica, marking, principal) scenarios. If Explain ever drifts from Compute, at
    /// least one of these must fail.</summary>
    public static TheoryData<string> Scenarios() => new()
    {
        "no-access", "role-only", "view-restriction-fails", "replica", "access-only",
        "edit-restriction-fails", "full-allow", "malformed-restriction",
        "two-failing-view-restrictions", "not-eligible", "unknown-category", "not-granted",
        "caveat", "all-marking-gates-fail",
    };

    private sealed record Scenario(
        AccessRule[] Grants, AccessRule[] Restrictions, bool Replica, ProtectiveMarking Marking, Principal Principal);

    private static readonly Guid PageId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AncestorId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private static Scenario BuildScenario(string name)
    {
        var everyoneAccess = AccessGrant("""{ "everyone": true }""");
        var everyoneEditor = RoleGrant(SpaceRole.Editor, """{ "everyone": true }""");
        var nzPrincipal = MakePrincipal(attributes: new() { ["nationality"] = ["NZ"] });
        var eligibleNz = MakePrincipal(attributes: new() { ["nationality"] = ["NZ"], [TestCatalogs.FruitClaim] = ["yes"] });
        var apple = ProtectiveMarking.Create(ClassificationLevel.Official, null, [TestCatalogs.Apple]);

        return name switch
        {
            "no-access" => new(
                [AccessGrant("""{ "group": "engineering" }"""), RoleGrant(SpaceRole.Editor, """{ "group": "engineering" }""")],
                [PageRestriction(PageId, PageAction.View, """{ "everyone": true }""")],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "role-only" => new(
                [everyoneEditor],
                [],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "view-restriction-fails" => new(
                [everyoneAccess, everyoneEditor],
                [PageRestriction(PageId, PageAction.View, """{ "attr": "nationality", "in": ["US"] }""")],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "replica" => new(
                [everyoneAccess, everyoneEditor],
                [PageRestriction(PageId, PageAction.Edit, """{ "everyone": true }""")],
                true, ProtectiveMarking.Baseline, nzPrincipal),
            "access-only" => new(
                [everyoneAccess],
                [],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "edit-restriction-fails" => new(
                [everyoneAccess, everyoneEditor],
                [
                    PageRestriction(AncestorId, PageAction.Edit, """{ "attr": "nationality", "in": ["US"] }"""),
                    PageRestriction(PageId, PageAction.View, """{ "everyone": true }"""),
                ],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "full-allow" => new(
                [everyoneAccess, everyoneEditor],
                [
                    PageRestriction(AncestorId, PageAction.View, """{ "attr": "nationality", "in": ["NZ", "US"] }"""),
                    PageRestriction(PageId, PageAction.Edit, """{ "everyone": true }"""),
                ],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "malformed-restriction" => new(
                [everyoneAccess, everyoneEditor],
                [PageRestriction(PageId, PageAction.View, """{ "bogus": true }""")],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "two-failing-view-restrictions" => new(
                [everyoneAccess, everyoneEditor],
                [
                    PageRestriction(AncestorId, PageAction.View, """{ "attr": "nationality", "in": ["US"] }"""),
                    PageRestriction(PageId, PageAction.View, """{ "group": "export-cleared" }"""),
                ],
                false, ProtectiveMarking.Baseline, nzPrincipal),
            "not-eligible" => new(
                [AccessGrant("""{ "everyone": true }""", TestCatalogs.Apple), everyoneEditor],
                [],
                false, apple, nzPrincipal),
            "unknown-category" => new(
                [everyoneAccess, everyoneEditor],
                [],
                false, ProtectiveMarking.Create(ClassificationLevel.Official, null, [new SelectorValue("CODEWORD", "ZEBRA")]), eligibleNz),
            "not-granted" => new(
                [everyoneAccess, everyoneEditor],
                [],
                false, apple, eligibleNz),
            "caveat" => new(
                [everyoneAccess, everyoneEditor],
                [],
                false, ProtectiveMarking.Create(ClassificationLevel.Official, ["US"]), nzPrincipal),
            "all-marking-gates-fail" => new(
                [everyoneAccess, everyoneEditor],
                [PageRestriction(PageId, PageAction.View, """{ "group": "export-cleared" }""")],
                false, ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"], [TestCatalogs.Apple]), nzPrincipal),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Explain_VerdictAndReasons_AgreeWithComputeExactly(string scenario)
    {
        var s = BuildScenario(scenario);

        var gate = Compute(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);
        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        // Record equality: CanView, CanEdit, AND both denial-reason strings must be
        // byte-identical - the reasons land in audit rows (§7) and the inspector UI
        // parses them (describeDenialReason.ts), so "same verdict, different reason"
        // is also a failure.
        Assert.Equal(gate, explained.Permission);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Explain_TheFirstFailingGateInTheList_IsTheReasonTheGateNamed(string scenario)
    {
        // The verdict is derived from the lists, so this is the property that makes the
        // lists trustworthy: whatever the gate reported is the first failed entry.
        var s = BuildScenario(scenario);

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        var firstFailedView = explained.ViewGates.FirstOrDefault(g => !g.Passed);
        Assert.Equal(firstFailedView?.Reason, explained.Permission.ViewDenialReason);
        if (firstFailedView is null)
        {
            var firstFailedEdit = explained.EditGates.FirstOrDefault(g => !g.Passed);
            Assert.Equal(firstFailedEdit?.Reason, explained.Permission.EditDenialReason);
        }
    }

    [Fact]
    public void Explain_NoAccess_ReportsTheOuterBoundaryAndNoRole()
    {
        var s = BuildScenario("no-access");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        Assert.False(explained.HasSpaceAccess);
        Assert.Null(explained.SpaceRole);
        Assert.False(explained.Permission.CanView);
        Assert.Equal("no-space-access", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_RoleOnly_HasNoSpaceAccess_ButStillReportsTheRoleItHolds()
    {
        // §6.5.2's Space-admin-who-cannot-read, seen through the inspector: the role is a
        // fact worth showing (it is why they can manage), and it rescues nothing.
        var s = BuildScenario("role-only");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        Assert.False(explained.HasSpaceAccess);
        Assert.Equal(SpaceRole.Editor, explained.SpaceRole);
        Assert.False(explained.Permission.CanView);
        Assert.False(explained.Permission.CanEdit);
        Assert.Equal("no-space-access", explained.Permission.ViewDenialReason);
        Assert.True(explained.MarkingWithheld);
    }

    [Fact]
    public void Explain_AccessOnly_ReportsNoRole_AndTheRoleGateAsTheEditReason()
    {
        var s = BuildScenario("access-only");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        Assert.True(explained.HasSpaceAccess);
        Assert.Null(explained.SpaceRole);
        Assert.True(explained.Permission.CanView);
        Assert.False(explained.Permission.CanEdit);
        Assert.Equal("insufficient-space-role", explained.Permission.EditDenialReason);
        Assert.Contains(explained.EditGates, g => g.Kind == GateKind.SpaceRole && !g.Passed);
    }

    [Fact]
    public void Explain_DoesNotShortCircuit_EveryFailingRestrictionIsReported()
    {
        // The gate stops at the first failing view restriction and can only ever
        // report one reason; §6.6 exists because "which single rule failed first" is
        // the least useful answer when several are in play.
        var s = BuildScenario("two-failing-view-restrictions");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        Assert.Equal(2, explained.ViewRestrictions.Count);
        Assert.All(explained.ViewRestrictions, check => Assert.False(check.Passed));
        // And the verdict's reason is still the FIRST failing one, matching the gate.
        var first = explained.ViewRestrictions[0];
        Assert.Equal($"restriction:{first.PageId}:{first.RuleId}", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_ListsEveryFailingGate_NotJustTheFirst()
    {
        // Level, eligibility, grant, caveat AND a restriction all fail here; the gate
        // names the level (the coarsest fact); the inspector shows all five.
        var s = BuildScenario("all-marking-gates-fail");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        Assert.Equal("classification:secret", explained.Permission.ViewDenialReason);
        Assert.Equal(
            new HashSet<GateKind>
            {
                GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant,
                GateKind.NationalCaveat, GateKind.ViewRestriction,
            },
            explained.ViewGates.Where(g => !g.Passed).Select(g => g.Kind).ToHashSet());
        Assert.Contains(explained.ViewGates, g => g.Kind == GateKind.SpaceAccess && g.Passed);
        // The selector entries carry the category and value in structured form, never in
        // the token (§15: the value is the marking's content).
        var eligibility = Assert.Single(explained.ViewGates, g => g.Kind == GateKind.SelectorEligibility);
        Assert.Equal("FRUIT", eligibility.SelectorCategory);
        Assert.Equal("APPLE", eligibility.SelectorValue);
        Assert.Equal("selector:not_eligible:FRUIT", eligibility.Reason);
    }

    [Fact]
    public void Explain_WhenSpaceAccessFails_StillEvaluatesMarkingGates_AndFlagsMarkingWithheld()
    {
        // The inspector wants the complete picture; the API must not show it to the
        // principal themselves, and MarkingWithheld is the datum it acts on (§21.8).
        var grants = new[] { AccessGrant("""{ "group": "engineering" }""", TestCatalogs.Apple) };
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"], [TestCatalogs.Apple]);
        var principal = MakePrincipal(attributes: new() { ["nationality"] = ["NZ"] });

        var explained = Explain(grants, [], false, marking, principal);

        Assert.False(explained.HasSpaceAccess);
        Assert.True(explained.MarkingWithheld);
        Assert.Empty(explained.GrantedSelectors);
        Assert.Equal("no-space-access", explained.Permission.ViewDenialReason);
        Assert.Equal(
            [GateKind.SpaceAccess, GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.NationalCaveat],
            explained.ViewGates.Select(g => g.Kind));
        // No access means no granted union: G fails regardless of what the grant carried.
        Assert.All(explained.ViewGates, g => Assert.False(g.Passed));
        // The edit half is listed too - replica passes, the role gate fails.
        Assert.Equal([GateKind.ReplicaReadOnly, GateKind.SpaceRole], explained.EditGates.Select(g => g.Kind));
    }

    [Fact]
    public void Explain_ViewRestrictions_AreProjectedFromGates()
    {
        var s = BuildScenario("edit-restriction-fails");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        var viewGate = Assert.Single(explained.ViewGates, g => g.Kind == GateKind.ViewRestriction);
        var viewCheck = Assert.Single(explained.ViewRestrictions);
        Assert.Equal(viewGate.RuleId, viewCheck.RuleId);
        Assert.Equal(viewGate.PageId, viewCheck.PageId);
        Assert.Equal(viewGate.ExpressionJson, viewCheck.ExpressionJson);
        Assert.Equal(PageAction.View, viewCheck.Action);
        Assert.True(viewCheck.Passed);

        var editGate = Assert.Single(explained.EditGates, g => g.Kind == GateKind.EditRestriction);
        var editCheck = Assert.Single(explained.EditRestrictions);
        Assert.Equal(editGate.RuleId, editCheck.RuleId);
        Assert.False(editCheck.Passed);
        Assert.Equal(SpaceRole.Editor, explained.SpaceRole);
        Assert.True(explained.Permission.CanView);
        Assert.False(explained.Permission.CanEdit);
    }

    [Fact]
    public void Explain_GateOrder_IsTheLadderInReportingOrder()
    {
        var s = BuildScenario("full-allow");
        var marking = ProtectiveMarking.Create(ClassificationLevel.Official, null, [TestCatalogs.Apple, TestCatalogs.North]);
        var principal = MakePrincipal(attributes: new() { ["nationality"] = ["NZ"], [TestCatalogs.FruitClaim] = ["yes"] });
        var grants = new[] { AccessGrant("""{ "everyone": true }""", TestCatalogs.Apple, TestCatalogs.North), s.Grants[1] };

        var explained = Explain(grants, s.Restrictions, false, marking, principal);

        Assert.True(explained.Permission.CanEdit);
        Assert.Equal(
            [
                GateKind.SpaceAccess, GateKind.Classification,
                GateKind.SelectorEligibility, GateKind.SelectorEligibility,
                GateKind.SelectorGrant, GateKind.SelectorGrant,
                GateKind.NationalCaveat, GateKind.ViewRestriction,
            ],
            explained.ViewGates.Select(g => g.Kind));
        Assert.Equal([GateKind.ReplicaReadOnly, GateKind.SpaceRole, GateKind.EditRestriction], explained.EditGates.Select(g => g.Kind));
        Assert.All(explained.ViewGates.Concat(explained.EditGates), g => Assert.True(g.Passed));
        Assert.Equal([TestCatalogs.Apple, TestCatalogs.North], explained.GrantedSelectors.OrderBy(x => x, SelectorValue.CanonicalOrder));
    }

    [Fact]
    public void Explain_MalformedRule_ReadsAsFailed_NeverAsPassed()
    {
        // §6.3 fail-closed, surfaced in the inspector: a rule that cannot be
        // evaluated shows as failed, exactly as the gate denies on it - an inspector
        // that displayed a malformed rule as "passed" would tell an admin the
        // opposite of what enforcement does.
        var s = BuildScenario("malformed-restriction");

        var explained = Explain(s.Grants, s.Restrictions, s.Replica, s.Marking, s.Principal);

        var check = Assert.Single(explained.ViewRestrictions);
        Assert.False(check.Passed);
        Assert.False(explained.Permission.CanView);
    }

    [Fact]
    public void Explain_ReplicaSpace_EditDeniedBeneathEveryGrant_ViewUnaffected()
    {
        var s = BuildScenario("replica");

        var explained = Explain(s.Grants, s.Restrictions, isReplicaSpace: true, s.Marking, s.Principal);

        Assert.True(explained.IsReplicaSpace);
        Assert.True(explained.Permission.CanView);
        Assert.False(explained.Permission.CanEdit);
        Assert.Equal("replica-read-only", explained.Permission.EditDenialReason);
        Assert.Contains(explained.EditGates, g => g.Kind == GateKind.ReplicaReadOnly && !g.Passed);
    }
}
