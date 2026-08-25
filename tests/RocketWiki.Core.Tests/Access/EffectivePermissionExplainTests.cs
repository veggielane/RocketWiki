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
/// precedence ladder (no-role, failing view restriction, replica, viewer-role,
/// failing edit restriction, full allow, malformed rules); (2) unlike the gate,
/// Explain evaluates EVERY restriction and reports each pass/fail rather than
/// stopping at the first failure — the whole reason it exists.
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

    private static AccessRule SpaceGrant(SpaceRole role, string expressionJson) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
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

    /// <summary>Every distinct branch of the verdict ladder, as (grants, restrictions,
    /// replica, principal) scenarios. If Explain's DeriveVerdict ever drifts from
    /// ComputeCore, at least one of these must fail.</summary>
    public static TheoryData<string> Scenarios() => new()
    {
        "no-role", "view-restriction-fails", "replica", "viewer-only",
        "edit-restriction-fails", "full-allow", "malformed-restriction",
        "two-failing-view-restrictions",
    };

    private static (AccessRule[] Grants, AccessRule[] Restrictions, bool Replica, Principal Principal) BuildScenario(string name)
    {
        var pageId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var ancestorId = new Guid("aaaaaaaa-0000-0000-0000-000000000002");
        var editorGrant = SpaceGrant(SpaceRole.Editor, """{ "everyone": true }""");
        var viewerGrant = SpaceGrant(SpaceRole.Viewer, """{ "everyone": true }""");
        var nzPrincipal = MakePrincipal(attributes: new() { ["nationality"] = ["NZ"] });

        return name switch
        {
            "no-role" => (
                [SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""")],
                [PageRestriction(pageId, PageAction.View, """{ "everyone": true }""")],
                false, nzPrincipal),
            "view-restriction-fails" => (
                [editorGrant],
                [PageRestriction(pageId, PageAction.View, """{ "attr": "nationality", "in": ["US"] }""")],
                false, nzPrincipal),
            "replica" => (
                [editorGrant],
                [PageRestriction(pageId, PageAction.Edit, """{ "everyone": true }""")],
                true, nzPrincipal),
            "viewer-only" => (
                [viewerGrant],
                [],
                false, nzPrincipal),
            "edit-restriction-fails" => (
                [editorGrant],
                [
                    PageRestriction(ancestorId, PageAction.Edit, """{ "attr": "nationality", "in": ["US"] }"""),
                    PageRestriction(pageId, PageAction.View, """{ "everyone": true }"""),
                ],
                false, nzPrincipal),
            "full-allow" => (
                [editorGrant],
                [
                    PageRestriction(ancestorId, PageAction.View, """{ "attr": "nationality", "in": ["NZ", "US"] }"""),
                    PageRestriction(pageId, PageAction.Edit, """{ "everyone": true }"""),
                ],
                false, nzPrincipal),
            "malformed-restriction" => (
                [editorGrant],
                [PageRestriction(pageId, PageAction.View, """{ "bogus": true }""")],
                false, nzPrincipal),
            "two-failing-view-restrictions" => (
                [editorGrant],
                [
                    PageRestriction(ancestorId, PageAction.View, """{ "attr": "nationality", "in": ["US"] }"""),
                    PageRestriction(pageId, PageAction.View, """{ "group": "export-cleared" }"""),
                ],
                false, nzPrincipal),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Explain_VerdictAndReasons_AgreeWithComputeExactly(string scenario)
    {
        var (grants, restrictions, replica, principal) = BuildScenario(scenario);

        var gate = EffectivePermissionCalculator.Compute(grants, restrictions, replica, ProtectiveMarking.Baseline, principal);
        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, replica, ProtectiveMarking.Baseline, principal);

        // Record equality: CanView, CanEdit, AND both denial-reason strings must be
        // byte-identical - the reasons land in audit rows (§7) and the inspector UI
        // parses them (describeDenialReason.ts), so "same verdict, different reason"
        // is also a failure.
        Assert.Equal(gate, explained.Permission);
    }

    [Fact]
    public void Explain_DoesNotShortCircuit_EveryFailingRestrictionIsReported()
    {
        // The gate stops at the first failing view restriction and can only ever
        // report one reason; §6.6 exists because "which single rule failed first" is
        // the least useful answer when several are in play.
        var (grants, restrictions, replica, principal) = BuildScenario("two-failing-view-restrictions");

        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, replica, ProtectiveMarking.Baseline, principal);

        Assert.Equal(2, explained.ViewRestrictions.Count);
        Assert.All(explained.ViewRestrictions, check => Assert.False(check.Passed));
        // And the verdict's reason is still the FIRST failing one, matching the gate.
        var first = explained.ViewRestrictions[0];
        Assert.Equal($"restriction:{first.PageId}:{first.RuleId}", explained.Permission.ViewDenialReason);
    }

    [Fact]
    public void Explain_ReportsPassedAndFailedChecks_ForBothActions()
    {
        var (grants, restrictions, replica, principal) = BuildScenario("edit-restriction-fails");

        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, replica, ProtectiveMarking.Baseline, principal);

        var viewCheck = Assert.Single(explained.ViewRestrictions);
        Assert.True(viewCheck.Passed);
        var editCheck = Assert.Single(explained.EditRestrictions);
        Assert.False(editCheck.Passed);
        Assert.Equal(SpaceRole.Editor, explained.SpaceRole);
        Assert.True(explained.Permission.CanView);
        Assert.False(explained.Permission.CanEdit);
    }

    [Fact]
    public void Explain_MalformedRule_ReadsAsFailed_NeverAsPassed()
    {
        // §6.3 fail-closed, surfaced in the inspector: a rule that cannot be
        // evaluated shows as failed, exactly as the gate denies on it - an inspector
        // that displayed a malformed rule as "passed" would tell an admin the
        // opposite of what enforcement does.
        var (grants, restrictions, replica, principal) = BuildScenario("malformed-restriction");

        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, replica, ProtectiveMarking.Baseline, principal);

        var check = Assert.Single(explained.ViewRestrictions);
        Assert.False(check.Passed);
        Assert.False(explained.Permission.CanView);
    }

    [Fact]
    public void Explain_ReplicaSpace_EditDeniedBeneathEveryGrant_ViewUnaffected()
    {
        var (grants, restrictions, _, principal) = BuildScenario("replica");

        var explained = EffectivePermissionCalculator.Explain(grants, restrictions, isReplicaSpace: true, ProtectiveMarking.Baseline, principal);

        Assert.True(explained.IsReplicaSpace);
        Assert.True(explained.Permission.CanView);
        Assert.False(explained.Permission.CanEdit);
        Assert.Equal("replica-read-only", explained.Permission.EditDenialReason);
    }
}
