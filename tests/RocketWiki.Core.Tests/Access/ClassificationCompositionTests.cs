using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21's single most important invariant: <b>classification can only ever
/// SUBTRACT access, never grant it.</b> These tests attack it from both sides — that no
/// grant, role, or passing restriction survives a failed clearance check, and that a
/// marking never rescues a permission that was already denied.
/// </summary>
public class ClassificationCompositionTests
{
    private static Principal PrincipalWith(params (string Key, string[] Values)[] attributes) =>
        Principal.Create(
            "user-sub",
            ["engineering"],
            attributes.Select(a => new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values)));

    private static AccessRule Grant(SpaceRole role) => new()
    {
        Id = Guid.NewGuid(),
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = Guid.NewGuid(),
        Role = role,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

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
    [InlineData(SpaceRole.Viewer)]
    [InlineData(SpaceRole.Editor)]
    [InlineData(SpaceRole.SpaceAdmin)]
    public void NoRole_HoweverHigh_SurvivesAFailedClearanceCheck(SpaceRole role)
    {
        // The space-admin case is the one that matters: §6.5 already says instance admins
        // do not read around restrictions, and §21 extends that to classification. There
        // is no role in this scheme that reads around a marking.
        var permission = EffectivePermissionCalculator.Compute(
            [Grant(role)],
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
        var permission = EffectivePermissionCalculator.Compute(
            [Grant(SpaceRole.SpaceAdmin)],
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
        var permission = EffectivePermissionCalculator.Compute(
            [Grant(SpaceRole.Editor)],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]),
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
            var permission = EffectivePermissionCalculator.Compute(
                [],
                [],
                isReplicaSpace: false,
                ProtectiveMarking.Create(level, null),
                PrincipalWith(("clearance", ["TOP_SECRET"])));

            Assert.False(permission.CanView);
            Assert.Equal("no-space-role", permission.ViewDenialReason);
        }
    }

    [Fact]
    public void NoSpaceRole_OutranksAClassificationFailure_SoTheOuterBoundaryIsReported()
    {
        var permission = EffectivePermissionCalculator.Compute(
            [],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.TopSecret, null),
            PrincipalWith());

        Assert.Equal("no-space-role", permission.ViewDenialReason);
    }

    [Fact]
    public void AFailedClearanceCheck_OutranksAFailingRestriction_InTheReportedReason()
    {
        // Ordering never changes the verdict (both are conjuncts) - only which reason the
        // audit row carries. Clearance first is the more actionable answer for a reviewer.
        var failingRestriction = PassingViewRestriction();
        failingRestriction.ExpressionJson = """{ "group": "nobody-is-in-this" }""";

        var permission = EffectivePermissionCalculator.Compute(
            [Grant(SpaceRole.Editor)],
            [failingRestriction],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            PrincipalWith());

        Assert.Equal("classification:secret", permission.ViewDenialReason);
    }

    [Fact]
    public void OnAReplica_ClearanceStillGatesView_AndTheReplicaRuleStillBeatsEdit()
    {
        var cleared = EffectivePermissionCalculator.Compute(
            [Grant(SpaceRole.SpaceAdmin)],
            [],
            isReplicaSpace: true,
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            PrincipalWith(("clearance", ["SECRET"])));

        Assert.True(cleared.CanView);
        Assert.False(cleared.CanEdit);
        Assert.Equal("replica-read-only", cleared.EditDenialReason);

        var uncleared = EffectivePermissionCalculator.Compute(
            [Grant(SpaceRole.SpaceAdmin)],
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
        var grants = new[] { Grant(SpaceRole.Editor) };
        var restrictions = new[] { PassingViewRestriction() };
        var marking = ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["GB"]);
        var principal = PrincipalWith(("clearance", ["SECRET"]), ("nationality", ["GB"]));

        var gate = EffectivePermissionCalculator.Compute(grants, restrictions, false, marking, principal);
        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, false, marking, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("classification:top_secret", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_AgreesWithTheGate_OnACaveatDenial()
    {
        var grants = new[] { Grant(SpaceRole.Editor) };
        var restrictions = new[] { PassingViewRestriction() };
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"]);
        var principal = PrincipalWith(("clearance", ["SECRET"]), ("nationality", ["GB"]));

        var gate = EffectivePermissionCalculator.Compute(grants, restrictions, false, marking, principal);
        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, false, marking, principal);

        Assert.Equal(gate, explained.Permission);
        Assert.Equal("caveat:eyes_only", explained.Permission.ViewDenialReason);
    }
}
