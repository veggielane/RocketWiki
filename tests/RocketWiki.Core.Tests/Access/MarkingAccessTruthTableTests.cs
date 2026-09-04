using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §6.4 / §21.2 as a truth table: canView is the conjunction of four gates —
/// space access (S), selector grant (G), national caveat (N), view restrictions (R) — and
/// the reason a denial names is the first failing gate in exactly that order. All 16 rows
/// are driven, not a sample: the ladder is the whole authorization model, and a gate that
/// could be skipped under one combination of the others is precisely the bug a sample
/// would miss. A second table drives the edit ladder (replica, role, edit restrictions)
/// over a passing view, and one more row drives the gate that has no toggle because it
/// has no principal-side input: a missing marking row denies every combination.
///
/// <para>The table was 64 rows over S, C, E, G, N, R. C (clearance against the level) and
/// E (a per-category eligibility claim) both read Keycloak attributes this deployment
/// does not carry, and both are gone; what is left is exactly the four gates that read
/// something real — a grant, a nationality, a rule.</para>
///
/// <para>Fixture: the confirmed test catalog (<see cref="TestCatalogs.Fruit"/>), a page
/// marked <c>UK SECRET APPLE UK EYES ONLY</c> with a <c>group: engineering</c> view
/// restriction. S toggles whether the access grant matches; G toggles whether the access
/// grant carries APPLE; N toggles nationality between UK and US; R toggles membership of
/// <c>engineering</c>. A role grant matching everyone is present throughout, so a passing
/// view row reaches the edit ladder — and so the S=false rows also prove a role grant
/// rescues nothing. The SECRET level is on every row and gates none of them.</para>
/// </summary>
public class MarkingAccessTruthTableTests
{
    private const string Everyone = """{ "everyone": true }""";
    private const string Nobody = """{ "group": "nobody-is-in-this" }""";
    private const string Engineering = """{ "group": "engineering" }""";

    private static readonly Guid PageId = new("aaaaaaaa-0000-0000-0000-00000000f00d");

    private static readonly ProtectiveMarking Marking =
        ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [TestCatalogs.Apple]);

    public static TheoryData<bool, bool, bool, bool> ViewRows()
    {
        var rows = new TheoryData<bool, bool, bool, bool>();
        for (var bits = 0; bits < 16; bits++)
        {
            rows.Add((bits & 8) != 0, (bits & 4) != 0, (bits & 2) != 0, (bits & 1) != 0);
        }

        return rows;
    }

    public static TheoryData<bool, bool, bool> EditRows()
    {
        var rows = new TheoryData<bool, bool, bool>();
        for (var bits = 0; bits < 8; bits++)
        {
            rows.Add((bits & 4) != 0, (bits & 2) != 0, (bits & 1) != 0);
        }

        return rows;
    }

    private sealed record Scenario(PermissionInputs Inputs, Principal Principal, AccessRule ViewRestriction, AccessRule EditRestriction);

    private static Scenario Build(
        bool s, bool g, bool n, bool r,
        bool replica = false, bool role = true, bool editR = true, ProtectiveMarking? marking = null)
    {
        var access = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = Guid.NewGuid(),
            ExpressionJson = s ? Everyone : Nobody,
        };
        if (g)
        {
            access.Selectors.Add(new AccessRuleSelector
            {
                AccessRuleId = access.Id, Category = TestCatalogs.Apple.Category, Value = TestCatalogs.Apple.Value,
            });
        }

        var roleGrant = new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = access.SpaceId,
            Role = SpaceRole.Editor,
            ExpressionJson = role ? Everyone : Nobody,
        };

        var viewRestriction = new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction, PageId = PageId, Action = PageAction.View, ExpressionJson = Engineering,
        };
        var editRestriction = new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction, PageId = PageId, Action = PageAction.Edit,
            ExpressionJson = editR ? Everyone : Nobody,
        };

        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>
        {
            new(CaveatGate.NationalityAttributeKey, [n ? "UK" : "US"]),
        };

        var principal = Principal.Create("user-sub", r ? ["engineering"] : [], attributes);
        var inputs = new PermissionInputs(
            [access, roleGrant], [viewRestriction, editRestriction], replica, marking ?? Marking, TestCatalogs.Fruit);
        return new Scenario(inputs, principal, viewRestriction, editRestriction);
    }

    [Theory]
    [MemberData(nameof(ViewRows))]
    public void View_IsTheConjunctionOfAllFourGates_AndNamesTheFirstFailingOneInOrder(bool s, bool g, bool n, bool r)
    {
        var scenario = Build(s, g, n, r);

        var permission = EffectivePermissionCalculator.Compute(scenario.Inputs, scenario.Principal);

        Assert.Equal(s && g && n && r, permission.CanView);

        var expectedReason =
            !s ? EffectivePermissionCalculator.NoSpaceAccessReason
            : !g ? "selector:not_granted:FRUIT"
            : !n ? CaveatGate.EyesOnlyReason
            : !r ? $"restriction:{PageId}:{scenario.ViewRestriction.Id}"
            : null;
        Assert.Equal(expectedReason, permission.ViewDenialReason);
        if (!permission.CanView)
        {
            // canEdit falls through canView: a view denial is an edit denial with the same reason.
            Assert.False(permission.CanEdit);
            Assert.Equal(expectedReason, permission.EditDenialReason);
        }
    }

    [Theory]
    [MemberData(nameof(ViewRows))]
    public void Explain_AgreesWithTheVerdict_AndListsExactlyTheFailingGates(bool s, bool g, bool n, bool r)
    {
        var scenario = Build(s, g, n, r);

        var permission = EffectivePermissionCalculator.Compute(scenario.Inputs, scenario.Principal);
        var explained = EffectivePermissionCalculator.Explain(scenario.Inputs, scenario.Principal);

        Assert.Equal(permission, explained.Permission);
        Assert.Equal(s, explained.HasSpaceAccess);
        Assert.Equal(!s, explained.MarkingWithheld);

        var expectedFailing = new HashSet<GateKind>();
        if (!s)
        {
            expectedFailing.Add(GateKind.SpaceAccess);
        }

        // No access grant, no granted union: G is unsatisfiable when S fails, whatever
        // the grant would have carried. Those rows deny on S; the inspector still lists G.
        if (!g || !s)
        {
            expectedFailing.Add(GateKind.SelectorGrant);
        }

        if (!n)
        {
            expectedFailing.Add(GateKind.NationalCaveat);
        }

        if (!r)
        {
            expectedFailing.Add(GateKind.ViewRestriction);
        }

        Assert.Equal(expectedFailing, explained.ViewGates.Where(x => !x.Passed).Select(x => x.Kind).ToHashSet());

        // Every gate is listed in the inspector form, passed or not, in the ladder's order.
        Assert.Equal(
            [GateKind.SpaceAccess, GateKind.SelectorGrant, GateKind.NationalCaveat, GateKind.ViewRestriction],
            explained.ViewGates.Select(x => x.Kind));
    }

    [Theory]
    [MemberData(nameof(ViewRows))]
    public void TheLevel_ChangesNoRow(bool s, bool g, bool n, bool r)
    {
        // The row's verdict and reason are identical under every level: the level is on
        // the marking for display and for the aggregate maximum, and the ladder never
        // reads it (§21.12).
        var reference = EffectivePermissionCalculator.Compute(Build(s, g, n, r).Inputs, Build(s, g, n, r).Principal);

        foreach (var level in Enum.GetValues<ClassificationLevel>())
        {
            var scenario = Build(s, g, n, r, marking: ProtectiveMarking.Create(level, ["UK"], [TestCatalogs.Apple]));
            var permission = EffectivePermissionCalculator.Compute(scenario.Inputs, scenario.Principal);

            Assert.Equal(reference.CanView, permission.CanView);
            Assert.Equal(reference.CanEdit, permission.CanEdit);
            // The restriction rule id differs per Build; compare the reason's shape.
            Assert.Equal(reference.ViewDenialReason?.Split(':')[0], permission.ViewDenialReason?.Split(':')[0]);
        }
    }

    [Theory]
    [MemberData(nameof(EditRows))]
    public void Edit_OverAPassingView_IsReplicaThenRoleThenEditRestrictions(bool replica, bool role, bool editR)
    {
        var scenario = Build(true, true, true, true, replica, role, editR);

        var permission = EffectivePermissionCalculator.Compute(scenario.Inputs, scenario.Principal);
        var explained = EffectivePermissionCalculator.Explain(scenario.Inputs, scenario.Principal);

        Assert.True(permission.CanView);
        Assert.Equal(!replica && role && editR, permission.CanEdit);

        var expectedReason =
            replica ? EffectivePermissionCalculator.ReplicaReadOnlyReason
            : !role ? EffectivePermissionCalculator.InsufficientSpaceRoleReason
            : !editR ? $"restriction:{PageId}:{scenario.EditRestriction.Id}"
            : null;
        Assert.Equal(expectedReason, permission.EditDenialReason);
        Assert.Equal(permission, explained.Permission);

        var expectedFailing = new HashSet<GateKind>();
        if (replica)
        {
            expectedFailing.Add(GateKind.ReplicaReadOnly);
        }

        if (!role)
        {
            expectedFailing.Add(GateKind.SpaceRole);
        }

        if (!editR)
        {
            expectedFailing.Add(GateKind.EditRestriction);
        }

        Assert.Equal(expectedFailing, explained.EditGates.Where(x => !x.Passed).Select(x => x.Kind).ToHashSet());
        Assert.Equal(
            [GateKind.ReplicaReadOnly, GateKind.SpaceRole, GateKind.EditRestriction],
            explained.EditGates.Select(x => x.Kind));
    }

    [Fact]
    public void ARoleGrant_RescuesNoRow_WhenTheAccessGrantDoesNotMatch()
    {
        // The S=false rows above already carry a matching Editor role grant. Stated once
        // more on its own, with a Space-admin, because it is the invariant the grant split
        // exists for (§6.4: roles never supersede access).
        var scenario = Build(false, true, true, true);
        var inputs = scenario.Inputs with
        {
            SpaceGrants = [scenario.Inputs.SpaceGrants[0], new AccessRule
            {
                Kind = AccessRuleKind.RoleGrant, SpaceId = Guid.NewGuid(), Role = SpaceRole.SpaceAdmin, ExpressionJson = Everyone,
            }],
        };

        var permission = EffectivePermissionCalculator.Compute(inputs, scenario.Principal);

        Assert.False(permission.CanView);
        Assert.Equal(EffectivePermissionCalculator.NoSpaceAccessReason, permission.ViewDenialReason);
    }

    // --- The gate with no toggle (design.md §21) -------------------------------------------

    [Fact]
    public void Compute_MarkingRowMissing_DeniesEveryone_EvenASpaceAdminWithEverySelector()
    {
        // The fail-open trap this whole removal had to step around. FailClosed used to
        // deny by being TOP SECRET; with the level out of the ladder it carries nothing a
        // gate reads - no selectors, no caveat - and would admit this caller outright.
        // The row is a Space-admin (the highest role), UK national, matching an access
        // grant that confers every value in the catalog, in engineering: every gate with
        // a toggle passes, and the verdict is still a denial with the availability token,
        // for every combination of the toggles behind it.
        foreach (var (s, g, n, r) in new[] { (true, true, true, true), (true, false, false, false), (false, true, true, true) })
        {
            var scenario = Build(s, g, n, r, marking: ProtectiveMarking.FailClosed);
            var inputs = scenario.Inputs with
            {
                SpaceGrants =
                [
                    scenario.Inputs.SpaceGrants[0],
                    new AccessRule
                    {
                        Kind = AccessRuleKind.RoleGrant, SpaceId = Guid.NewGuid(), Role = SpaceRole.SpaceAdmin, ExpressionJson = Everyone,
                    },
                ],
            };
            foreach (var selector in new[] { TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South })
            {
                inputs.SpaceGrants[0].Selectors.Add(new AccessRuleSelector
                {
                    AccessRuleId = inputs.SpaceGrants[0].Id, Category = selector.Category, Value = selector.Value,
                });
            }

            var permission = EffectivePermissionCalculator.Compute(inputs, scenario.Principal);
            var explained = EffectivePermissionCalculator.Explain(inputs, scenario.Principal);

            Assert.False(permission.CanView);
            Assert.False(permission.CanEdit);
            Assert.Equal(s ? MarkingGate.UnavailableReason : EffectivePermissionCalculator.NoSpaceAccessReason, permission.ViewDenialReason);
            Assert.Equal(permission, explained.Permission);
            Assert.Contains(explained.ViewGates, gate => gate.Kind == GateKind.MarkingUnavailable && !gate.Passed);
            // Nothing else about the marking is listed: there is no marking to evaluate.
            Assert.DoesNotContain(explained.ViewGates, gate => gate.Kind is GateKind.SelectorGrant or GateKind.NationalCaveat);
        }
    }
}
