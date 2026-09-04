using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21's single most important invariant: <b>a marking can only ever SUBTRACT
/// access, never grant it.</b> These tests attack it from both sides — that no grant,
/// role, or passing restriction survives a failed selector or caveat check, and that a
/// marking never rescues a permission that was already denied. Fixtures pair an access
/// grant with each role grant (§6.4: roles confer no visibility).
///
/// <para>This file was named for the classification level, which used to be the first
/// subtracting gate: an uncleared caller failed a SECRET page here however high their
/// role. That gate is gone — this deployment carries no clearance attribute — and the
/// level subtracts nothing now; the cases that proved it did are retired, and the cases
/// that prove <i>selectors</i> and the <i>caveat</i> compose by subtraction, and that a
/// role grant contributes nothing to either, are what remain. The name stays so the
/// file's history reads: this is where "the marking subtracts" has always been pinned.</para>
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

    private static readonly ProtectiveMarking Apple =
        ProtectiveMarking.Create(ClassificationLevel.Official, null, [TestCatalogs.Apple]);

    [Theory]
    [InlineData(null)]
    [InlineData(SpaceRole.Editor)]
    [InlineData(SpaceRole.SpaceAdmin)]
    public void NoRole_HoweverHigh_SurvivesAFailedSelectorCheck(SpaceRole? role)
    {
        // The space-admin case is the one that matters: §6.5 already says instance admins
        // do not read around restrictions, and §21 extends that to a marking. There is no
        // role in this scheme that reads around a selector - and "no role at all"
        // (access only) reads around nothing either. The access grant carries no APPLE.
        AccessRule[] grants = role is null ? [Access()] : [Access(), Grant(role.Value)];

        var permission = Compute(grants, [], isReplicaSpace: false, Apple, PrincipalWith());

        Assert.False(permission.CanView);
        Assert.False(permission.CanEdit);
        Assert.Equal("selector:not_granted:FRUIT", permission.ViewDenialReason);
    }

    [Fact]
    public void APassingRestriction_DoesNotRescueAFailedSelectorCheck()
    {
        var permission = Compute(
            [Access(), Grant(SpaceRole.SpaceAdmin)],
            [PassingViewRestriction()],
            isReplicaSpace: false,
            Apple,
            PrincipalWith());

        Assert.False(permission.CanView);
    }

    [Fact]
    public void CanEdit_IsFalseWheneverTheCaveatDeniesCanView_BecauseEditFallsThroughView()
    {
        // §6.4: canEdit(page) = canView(page) AND ... - so an editor who fails the
        // caveat gate loses edit as well, without the gate needing to know about edit.
        var permission = Compute(
            [Access(), Grant(SpaceRole.Editor)],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]),
            PrincipalWith(("nationality", ["NZ"])));

        Assert.False(permission.CanView);
        Assert.False(permission.CanEdit);
        Assert.Equal("caveat:eyes_only", permission.EditDenialReason);
    }

    [Fact]
    public void AMarkingTheCallerPasses_NeverTurnsADenialIntoAnAllow()
    {
        // The subtract-only direction, stated as a test: no marking value produces
        // canView on a page whose space grants the caller nothing - not the baseline,
        // not any level, not a caveat the caller is in.
        foreach (var level in Enum.GetValues<ClassificationLevel>())
        foreach (string[] countries in new[] { Array.Empty<string>(), ["UK"] })
        {
            var permission = Compute(
                [],
                [],
                isReplicaSpace: false,
                ProtectiveMarking.Create(level, countries),
                PrincipalWith(("nationality", ["UK"])));

            Assert.False(permission.CanView);
            Assert.Equal("no-space-access", permission.ViewDenialReason);
        }
    }

    [Fact]
    public void TheLevel_SubtractsNothing_AtAnyRung()
    {
        // The retired invariant, inverted and pinned so it cannot creep back: a caller
        // with no clearance attribute (nobody has one) reads a page at every level, given
        // access. If the level ever gates again, this is the test that says so.
        foreach (var level in Enum.GetValues<ClassificationLevel>())
        {
            var permission = Compute([Access()], [], isReplicaSpace: false, ProtectiveMarking.Create(level, null), PrincipalWith());

            Assert.True(permission.CanView, $"{level} should not gate");
        }
    }

    [Fact]
    public void NoSpaceAccess_OutranksASelectorFailure_SoTheOuterBoundaryIsReported()
    {
        var permission = Compute([], [], isReplicaSpace: false, Apple, PrincipalWith());

        Assert.Equal("no-space-access", permission.ViewDenialReason);
    }

    [Fact]
    public void AFailedSelectorCheck_OutranksAFailingRestriction_InTheReportedReason()
    {
        // Ordering never changes the verdict (both are conjuncts) - only which reason the
        // audit row carries. The marking first is the more actionable answer for a reviewer.
        var failingRestriction = PassingViewRestriction();
        failingRestriction.ExpressionJson = """{ "group": "nobody-is-in-this" }""";

        var permission = Compute(
            [Access(), Grant(SpaceRole.Editor)],
            [failingRestriction],
            isReplicaSpace: false,
            Apple,
            PrincipalWith());

        Assert.Equal("selector:not_granted:FRUIT", permission.ViewDenialReason);
    }

    [Fact]
    public void OnAReplica_TheMarkingStillGatesView_AndTheReplicaRuleStillBeatsEdit()
    {
        var granted = Access();
        granted.Selectors.Add(new AccessRuleSelector { AccessRuleId = granted.Id, Category = TestCatalogs.Apple.Category, Value = TestCatalogs.Apple.Value });

        var admitted = Compute([granted, Grant(SpaceRole.SpaceAdmin)], [], isReplicaSpace: true, Apple, PrincipalWith());

        Assert.True(admitted.CanView);
        Assert.False(admitted.CanEdit);
        Assert.Equal("replica-read-only", admitted.EditDenialReason);

        var refused = Compute([Access(), Grant(SpaceRole.SpaceAdmin)], [], isReplicaSpace: true, Apple, PrincipalWith());

        Assert.False(refused.CanView);
    }

    [Fact]
    public void Explain_AgreesWithTheGate_OnACaveatDenial()
    {
        // §6.6: the inspector exists to explain audit rows, and the two disagreeing would
        // make both useless. The caveat gate has to be mirrored in Explain's verdict
        // derivation, and this is what catches it if it is not.
        var grants = new[] { Access(), Grant(SpaceRole.Editor) };
        var restrictions = new[] { PassingViewRestriction() };
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"]);
        var principal = PrincipalWith(("nationality", ["UK"]));

        var gate = Compute(grants, restrictions, false, marking, principal);
        var explained = Explain(grants, restrictions, false, marking, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("caveat:eyes_only", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void AnAccessGrantWithEveryOtherSelector_DoesNotRescueTheOneMissing()
    {
        // G is per value: the most generous grant a space can hold, short of the value on
        // the page, still fails it.
        var generous = Access();
        foreach (var selector in new[] { TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South })
        {
            generous.Selectors.Add(new AccessRuleSelector { AccessRuleId = generous.Id, Category = selector.Category, Value = selector.Value });
        }

        var permission = Compute([generous, Grant(SpaceRole.SpaceAdmin)], [], isReplicaSpace: false, Apple, PrincipalWith());

        Assert.False(permission.CanView);
        Assert.Equal("selector:not_granted:FRUIT", permission.ViewDenialReason);
    }

    [Fact]
    public void ARoleGrant_NeverContributesSelectors()
    {
        // Unrepresentable through the service (a role grant may carry no selector rows),
        // but the calculator is the last line: rows on a role grant, however they got
        // there, are not part of the granted union.
        var role = Grant(SpaceRole.SpaceAdmin);
        role.Selectors.Add(new AccessRuleSelector { AccessRuleId = role.Id, Category = TestCatalogs.Apple.Category, Value = TestCatalogs.Apple.Value });

        var permission = Compute([Access(), role], [], isReplicaSpace: false, Apple, PrincipalWith());

        Assert.False(permission.CanView);
        Assert.Equal("selector:not_granted:FRUIT", permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_AgreesWithTheGate_OnASelectorDenial()
    {
        var grants = new[] { Access(), Grant(SpaceRole.Editor) };
        var principal = PrincipalWith();

        var gate = Compute(grants, [], false, Apple, principal);
        var explained = Explain(grants, [], false, Apple, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("selector:not_granted:FRUIT", explained.Permission.ViewDenialReason);
    }
}
