using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21's single most important invariant: <b>classification can only ever
/// SUBTRACT access, never grant it.</b> These tests attack it from both sides — that no
/// grant, role, or passing restriction survives a failed clearance check, and that a
/// marking never rescues a permission that was already denied. Fixtures pair an access
/// grant with each role grant (§6.4: roles confer no visibility). The selector gates
/// (§21.15) get the same treatment: no access grant, however generous, rescues a failed
/// eligibility check, and no role grant ever contributes a selector.
/// </summary>
public class ClassificationCompositionTests
{
    private static Principal PrincipalWith(params (string Key, string[] Values)[] attributes) =>
        Principal.Create(
            "user-sub",
            ["engineering"],
            attributes.Select(a => new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values)));

    private static AccessRule Access() => new()
    {
        Id = Guid.NewGuid(),
        Kind = AccessRuleKind.AccessGrant,
        SpaceId = Guid.NewGuid(),
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static AccessRule Grant(SpaceRole role) => new()
    {
        Id = Guid.NewGuid(),
        Kind = AccessRuleKind.RoleGrant,
        SpaceId = Guid.NewGuid(),
        Role = role,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

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

    private static AccessRule PassingViewRestriction() => new()
    {
        Id = Guid.NewGuid(),
        Kind = AccessRuleKind.PageRestriction,
        PageId = Guid.NewGuid(),
        Action = PageAction.View,
        ExpressionJson = """{ "group": "engineering" }""",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    [Theory]
    [InlineData(null)]
    [InlineData(SpaceRole.Editor)]
    [InlineData(SpaceRole.SpaceAdmin)]
    public void NoRole_HoweverHigh_SurvivesAFailedClearanceCheck(SpaceRole? role)
    {
        // The space-admin case is the one that matters: §6.5 already says instance admins
        // do not read around restrictions, and §21 extends that to classification. There
        // is no role in this scheme that reads around a marking - and "no role at all"
        // (access only) reads around nothing either.
        AccessRule[] grants = role is null ? [Access()] : [Access(), Grant(role.Value)];

        var permission = Compute(
            grants,
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.TopSecret, null),
            PrincipalWith());

        Assert.False(permission.CanView);
        Assert.False(permission.CanEdit);
        Assert.Equal("classification:top_secret", permission.ViewDenialReason);
    }

    [Fact]
    public void APassingRestriction_DoesNotRescueAFailedClearanceCheck()
    {
        var permission = Compute(
            [Access(), Grant(SpaceRole.SpaceAdmin)],
            [PassingViewRestriction()],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            PrincipalWith(("clearance", ["OFFICIAL_SENSITIVE"])));

        Assert.False(permission.CanView);
    }

    [Fact]
    public void CanEdit_IsFalseWheneverClearanceDeniesCanView_BecauseEditFallsThroughView()
    {
        // §6.4: canEdit(page) = canView(page) AND ... - so an editor who fails the
        // clearance gate loses edit as well, without the gate needing to know about edit.
        var permission = Compute(
            [Access(), Grant(SpaceRole.Editor)],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]),
            PrincipalWith(("clearance", ["TOP_SECRET"]), ("nationality", ["NZ"])));

        Assert.False(permission.CanView);
        Assert.False(permission.CanEdit);
        Assert.Equal("caveat:eyes_only", permission.EditDenialReason);
    }

    [Fact]
    public void AMarkingTheCallerIsClearedFor_NeverTurnsADenialIntoAnAllow()
    {
        // The subtract-only direction, stated as a test: no marking value produces
        // canView on a page whose space grants the caller nothing.
        foreach (var level in Enum.GetValues<ClassificationLevel>())
        {
            var permission = Compute(
                [],
                [],
                isReplicaSpace: false,
                ProtectiveMarking.Create(level, null),
                PrincipalWith(("clearance", ["TOP_SECRET"])));

            Assert.False(permission.CanView);
            Assert.Equal("no-space-access", permission.ViewDenialReason);
        }
    }

    [Fact]
    public void NoSpaceAccess_OutranksAClassificationFailure_SoTheOuterBoundaryIsReported()
    {
        var permission = Compute(
            [],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.TopSecret, null),
            PrincipalWith());

        Assert.Equal("no-space-access", permission.ViewDenialReason);
    }

    [Fact]
    public void AFailedClearanceCheck_OutranksAFailingRestriction_InTheReportedReason()
    {
        // Ordering never changes the verdict (both are conjuncts) - only which reason the
        // audit row carries. Clearance first is the more actionable answer for a reviewer.
        var failingRestriction = PassingViewRestriction();
        failingRestriction.ExpressionJson = """{ "group": "nobody-is-in-this" }""";

        var permission = Compute(
            [Access(), Grant(SpaceRole.Editor)],
            [failingRestriction],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            PrincipalWith());

        Assert.Equal("classification:secret", permission.ViewDenialReason);
    }

    [Fact]
    public void OnAReplica_ClearanceStillGatesView_AndTheReplicaRuleStillBeatsEdit()
    {
        var cleared = Compute(
            [Access(), Grant(SpaceRole.SpaceAdmin)],
            [],
            isReplicaSpace: true,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            PrincipalWith(("clearance", ["SECRET"])));

        Assert.True(cleared.CanView);
        Assert.False(cleared.CanEdit);
        Assert.Equal("replica-read-only", cleared.EditDenialReason);

        var uncleared = Compute(
            [Access(), Grant(SpaceRole.SpaceAdmin)],
            [],
            isReplicaSpace: true,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            PrincipalWith());

        Assert.False(uncleared.CanView);
    }

    [Fact]
    public void Explain_AgreesWithTheGate_OnAClassificationDenial()
    {
        // §6.6: the inspector exists to explain audit rows, and the two disagreeing would
        // make both useless. The classification gate has to be mirrored in Explain's
        // verdict derivation, and this is what catches it if it is not.
        var grants = new[] { Access(), Grant(SpaceRole.Editor) };
        var restrictions = new[] { PassingViewRestriction() };
        var marking = ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["UK"]);
        var principal = PrincipalWith(("clearance", ["SECRET"]), ("nationality", ["UK"]));

        var gate = Compute(grants, restrictions, false, marking, principal);
        var explained = Explain(grants, restrictions, false, marking, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("classification:top_secret", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_AgreesWithTheGate_OnACaveatDenial()
    {
        var grants = new[] { Access(), Grant(SpaceRole.Editor) };
        var restrictions = new[] { PassingViewRestriction() };
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"]);
        var principal = PrincipalWith(("clearance", ["SECRET"]), ("nationality", ["UK"]));

        var gate = Compute(grants, restrictions, false, marking, principal);
        var explained = Explain(grants, restrictions, false, marking, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("caveat:eyes_only", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void AnAccessGrantWithEverySelector_DoesNotRescueAFailedEligibilityCheck()
    {
        // G is a fact about the space; E is a fact about the principal and the instance.
        // The most generous grant a space can hold cannot make a principal eligible.
        var generous = Access();
        foreach (var selector in new[] { TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South })
        {
            generous.Selectors.Add(new AccessRuleSelector { AccessRuleId = generous.Id, Category = selector.Category, Value = selector.Value });
        }

        var permission = Compute(
            [generous, Grant(SpaceRole.SpaceAdmin)],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [TestCatalogs.Apple]),
            PrincipalWith(("clearance", ["SECRET"]))); // no fruit claim

        Assert.False(permission.CanView);
        Assert.Equal("selector:not_eligible:FRUIT", permission.ViewDenialReason);
    }

    [Fact]
    public void ARoleGrant_NeverContributesSelectors()
    {
        // Unrepresentable through the service (a role grant may carry no selector rows),
        // but the calculator is the last line: rows on a role grant, however they got
        // there, are not part of the granted union.
        var role = Grant(SpaceRole.SpaceAdmin);
        role.Selectors.Add(new AccessRuleSelector { AccessRuleId = role.Id, Category = TestCatalogs.Apple.Category, Value = TestCatalogs.Apple.Value });

        var permission = Compute(
            [Access(), role],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Official, null, [TestCatalogs.Apple]),
            PrincipalWith(("clearance", ["SECRET"]), ("fruit", ["yes"])));

        Assert.False(permission.CanView);
        Assert.Equal("selector:not_granted:FRUIT", permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_AgreesWithTheGate_OnASelectorDenial()
    {
        var grants = new[] { Access(), Grant(SpaceRole.Editor) };
        var marking = ProtectiveMarking.Create(ClassificationLevel.Official, null, [TestCatalogs.Apple]);
        var principal = PrincipalWith(("clearance", ["SECRET"]), ("fruit", ["yes"]));

        var gate = Compute(grants, [], false, marking, principal);
        var explained = Explain(grants, [], false, marking, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("selector:not_granted:FRUIT", explained.Permission.ViewDenialReason);
    }
}
