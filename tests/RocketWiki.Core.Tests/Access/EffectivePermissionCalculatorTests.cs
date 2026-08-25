using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

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

    private static AccessRule SpaceGrant(SpaceRole role, string expressionJson, Guid? spaceId = null) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId ?? Guid.NewGuid(),
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

    // --- ComputeSpaceRole -----------------------------------------------------------

    [Fact]
    public void ComputeSpaceRole_NoGrantsMatch_ReturnsNull()
    {
        var principal = MakePrincipal();
        var grants = new[] { SpaceGrant(SpaceRole.Viewer, """{ "group": "engineering" }""") };

        var role = EffectivePermissionCalculator.ComputeSpaceRole(grants, principal);

        Assert.Null(role);
    }

    [Fact]
    public void ComputeSpaceRole_MultipleMatchingGrants_ReturnsHighest()
    {
        // design.md §6.4: multiple grants OR together; your role is the highest you satisfy.
        var principal = MakePrincipal(groups: new[] { "everyone-group", "engineering" });
        var grants = new[]
        {
            SpaceGrant(SpaceRole.Viewer, """{ "everyone": true }"""),
            SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }"""),
        };

        var role = EffectivePermissionCalculator.ComputeSpaceRole(grants, principal);

        Assert.Equal(SpaceRole.Editor, role);
    }

    [Fact]
    public void ComputeSpaceRole_OpenSpace_EveryoneGrantsViewer()
    {
        var principal = MakePrincipal();
        var grants = new[] { SpaceGrant(SpaceRole.Viewer, """{ "everyone": true }""") };

        var role = EffectivePermissionCalculator.ComputeSpaceRole(grants, principal);

        Assert.Equal(SpaceRole.Viewer, role);
    }

    [Fact]
    public void ComputeSpaceRole_MalformedGrant_IsTreatedAsNonMatching()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.SpaceAdmin, """{ "bogus": true }""") };

        var role = EffectivePermissionCalculator.ComputeSpaceRole(grants, principal);

        Assert.Null(role);
    }

    // --- Compute: baseline behaviour -------------------------------------------------

    [Fact]
    public void Compute_NoSpaceRole_DeniesViewAndEdit()
    {
        var principal = MakePrincipal();
        var grants = new[] { SpaceGrant(SpaceRole.Viewer, """{ "group": "engineering" }""") };

        var result = EffectivePermissionCalculator.Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public void Compute_ViewerRoleNoRestrictions_CanViewButNotEdit()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Viewer, """{ "group": "engineering" }""") };

        var result = EffectivePermissionCalculator.Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("insufficient-space-role", result.EditDenialReason);
    }

    [Fact]
    public void Compute_EditorRoleNoRestrictions_CanViewAndEdit()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };

        var result = EffectivePermissionCalculator.Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.True(result.CanEdit);
    }

    // --- Restriction accumulation down the ancestor chain ----------------------------

    [Fact]
    public void Compute_RestrictionOnPageItself_BlocksView()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "top-secret" }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

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
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(ancestorPageId, PageAction.View, """{ "group": "top-secret" }"""),
            PageRestriction(childPageId, PageAction.View, """{ "everyone": true }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
    }

    [Fact]
    public void Compute_EditRestrictionFails_CanViewButNotEdit()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.Edit, """{ "group": "senior-engineering" }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public void Compute_ViewRestrictionDoesNotAffectUnrelatedAction_EditStillCheckedIndependently()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering", "senior-engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "engineering" }"""),
            PageRestriction(pageId, PageAction.Edit, """{ "group": "senior-engineering" }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.True(result.CanEdit);
    }

    // --- Replica spaces: unconditional read-only -------------------------------------

    [Fact]
    public void Compute_ReplicaSpace_CanViewButCanNeverEdit_EvenWithSpaceAdminGrant()
    {
        // design.md §6.4/§12: canEdit is unconditionally false on a replica, beating
        // every grant - here the principal is even SpaceAdmin.
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.SpaceAdmin, """{ "group": "engineering" }""") };

        var result = EffectivePermissionCalculator.Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: true, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit);
        Assert.Equal("replica-read-only", result.EditDenialReason);
    }

    [Fact]
    public void Compute_ReplicaSpace_ViewRestrictionsStillApply()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.SpaceAdmin, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "top-secret" }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: true, ProtectiveMarking.Baseline, principal);

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
            groups: new[] { "admins" },
            attributes: new() { ["nationality"] = new[] { "FR" } });
        var grants = new[] { SpaceGrant(SpaceRole.SpaceAdmin, """{ "group": "admins" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "attr": "nationality", "in": ["NZ", "US"] }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
    }

    // --- Fail-closed: missing attribute / unknown group / malformed rule -------------

    [Fact]
    public void Compute_MissingAttribute_FailsClosedOnRestriction()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering" }); // no nationality attribute at all
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "attr": "nationality", "in": ["NZ", "US"] }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
    }

    [Fact]
    public void Compute_UnknownGroupOnThisInstance_FailsClosedOnRestriction()
    {
        // design.md §12: a synced restriction naming a group unknown on this instance
        // (realm mismatch) matches nobody.
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "group": "clearance-level-9" }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
    }

    [Fact]
    public void Compute_MalformedRestriction_DeniesRatherThanThrowingOrDefaultAllowing()
    {
        var pageId = Guid.NewGuid();
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[] { SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""") };
        var restrictions = new[]
        {
            PageRestriction(pageId, PageAction.View, """{ "not": { "group": "engineering" } }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, restrictions, isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.False(result.CanView);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public void Compute_MalformedSpaceGrant_ContributesNoRoleRatherThanThrowing()
    {
        var principal = MakePrincipal(groups: new[] { "engineering" });
        var grants = new[]
        {
            SpaceGrant(SpaceRole.SpaceAdmin, """{ "malformed": true }"""),
            SpaceGrant(SpaceRole.Viewer, """{ "group": "engineering" }"""),
        };

        var result = EffectivePermissionCalculator.Compute(grants, Array.Empty<AccessRule>(), isReplicaSpace: false, ProtectiveMarking.Baseline, principal);

        Assert.True(result.CanView);
        Assert.False(result.CanEdit); // only the valid Viewer grant counted, not the malformed SpaceAdmin one
    }
}
