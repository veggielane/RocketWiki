using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.2: the one composition of the four marking gates, C, E, G, N, in that
/// reporting order. These pin the order, that CheckAll lists every failure while Check
/// stops at the first, that the two can never disagree, and that the prefix is read by
/// none of the four.
/// </summary>
public class MarkingGateTests
{
    private static readonly IReadOnlySet<SelectorValue> Nothing = new HashSet<SelectorValue>();

    private static IReadOnlySet<SelectorValue> Granted(params SelectorValue[] values) => new HashSet<SelectorValue>(values);

    private static Principal PrincipalWith(params (string Key, string[] Values)[] attributes) =>
        Principal.Create(
            "user-sub",
            [],
            attributes.Select(a => new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values)));

    /// <summary>SECRET APPLE UK EYES ONLY — the marking that can fail all four gates.</summary>
    private static readonly ProtectiveMarking Everything =
        ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [TestCatalogs.Apple]);

    private static Principal Cleared(string level, string nationality, string fruit) =>
        PrincipalWith(("clearance", [level]), ("nationality", [nationality]), (TestCatalogs.FruitClaim, [fruit]));

    [Fact]
    public void Check_PassesWhenEveryGatePasses()
    {
        var principal = Cleared("SECRET", "UK", "yes");

        Assert.True(MarkingGate.Check(Everything, principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)).IsAllowed);
        Assert.All(MarkingGate.CheckAll(Everything, principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)), c => Assert.True(c.Passed));
    }

    [Fact]
    public void Order_IsClassificationThenEligibilityThenGrantThenCaveat()
    {
        // Every gate fails; knock them down one at a time from the front and the reported
        // reason must move to the next gate in C, E, G, N order.
        var failsAll = Cleared("OFFICIAL", "US", "no");
        Assert.Equal("classification:secret", MarkingGate.Check(Everything, failsAll, TestCatalogs.Fruit, Nothing).DenialReason);

        var failsEgn = Cleared("SECRET", "US", "no");
        Assert.Equal("selector:not_eligible:FRUIT", MarkingGate.Check(Everything, failsEgn, TestCatalogs.Fruit, Nothing).DenialReason);

        var failsGn = Cleared("SECRET", "US", "yes");
        Assert.Equal("selector:not_granted:FRUIT", MarkingGate.Check(Everything, failsGn, TestCatalogs.Fruit, Nothing).DenialReason);

        var failsN = Cleared("SECRET", "US", "yes");
        Assert.Equal("caveat:eyes_only", MarkingGate.Check(Everything, failsN, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)).DenialReason);
    }

    [Fact]
    public void CheckAll_ListsEveryFailure()
    {
        var failsAll = Cleared("OFFICIAL", "US", "no");

        var checks = MarkingGate.CheckAll(Everything, failsAll, TestCatalogs.Fruit, Nothing);

        Assert.Equal(
            [GateKind.Classification, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.NationalCaveat],
            checks.Select(c => c.Kind));
        Assert.All(checks, c => Assert.False(c.Passed));
        Assert.Equal(
            ["classification:secret", "selector:not_eligible:FRUIT", "selector:not_granted:FRUIT", "caveat:eyes_only"],
            checks.Select(c => c.Reason));
    }

    [Fact]
    public void CheckAll_FirstFailure_IsExactlyWhatCheckReports()
    {
        // The explanation can never disagree with the gate: over every combination of
        // the four principal facts, the first failed entry in CheckAll carries the same
        // reason Check returns, and CheckAll is all-passed exactly when Check allows.
        foreach (var level in new[] { "OFFICIAL", "SECRET" })
        foreach (var nationality in new[] { "UK", "US" })
        foreach (var fruit in new[] { "yes", "no" })
        foreach (var granted in new[] { Nothing, Granted(TestCatalogs.Apple) })
        {
            var principal = Cleared(level, nationality, fruit);
            var verdict = MarkingGate.Check(Everything, principal, TestCatalogs.Fruit, granted);
            var checks = MarkingGate.CheckAll(Everything, principal, TestCatalogs.Fruit, granted);

            Assert.Equal(verdict.IsAllowed, checks.All(c => c.Passed));
            Assert.Equal(verdict.DenialReason, checks.FirstOrDefault(c => !c.Passed)?.Reason);
        }
    }

    [Fact]
    public void Check_IgnoresPrefix()
    {
        // design.md §21.12, restated over the composed gate: no prefix value changes any
        // verdict or any reason, with selectors present or absent.
        foreach (var selectors in new[] { Array.Empty<SelectorValue>(), [TestCatalogs.Apple] })
        foreach (var principal in new[] { Cleared("OFFICIAL", "US", "no"), Cleared("SECRET", "UK", "yes"), PrincipalWith() })
        foreach (var granted in new[] { Nothing, Granted(TestCatalogs.Apple) })
        {
            var bare = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], selectors, prefix: null);
            var prefixed = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], selectors, "ZZPREFIXSENTINELZZ");

            var without = MarkingGate.Check(bare, principal, TestCatalogs.Fruit, granted);
            var with = MarkingGate.Check(prefixed, principal, TestCatalogs.Fruit, granted);

            Assert.Equal(without.IsAllowed, with.IsAllowed);
            Assert.Equal(without.DenialReason, with.DenialReason);
            if (with.DenialReason is { } reason)
            {
                Assert.DoesNotContain("ZZPREFIXSENTINELZZ", reason, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void NoSelectors_ReducesToTheClearanceGate()
    {
        // A marking without selectors goes through exactly C then N - byte-identical to
        // ClearanceGate.Check, which is what lets the composed gate replace it everywhere.
        var plain = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        foreach (var principal in new[] { Cleared("OFFICIAL", "US", "no"), Cleared("SECRET", "US", "no"), Cleared("SECRET", "UK", "no"), PrincipalWith() })
        {
            var composed = MarkingGate.Check(plain, principal, SelectorCatalog.Empty, Nothing);
            var clearance = ClearanceGate.Check(plain, principal);

            Assert.Equal(clearance.IsAllowed, composed.IsAllowed);
            Assert.Equal(clearance.DenialReason, composed.DenialReason);
        }
    }
}
