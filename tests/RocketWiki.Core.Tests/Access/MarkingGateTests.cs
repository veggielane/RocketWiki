using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.2: the one composition of the marking gates — availability, G, N, in
/// that reporting order. These pin the order, that CheckAll lists every failure while
/// Check stops at the first, that the two can never disagree, that neither the level nor
/// the prefix is read by any gate, and — the trap — that a missing marking row denies
/// everyone by its own gate rather than by a level nobody compares any more.
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

    /// <summary>SECRET APPLE UK EYES ONLY — the marking that can fail both content gates.</summary>
    private static readonly ProtectiveMarking Everything =
        ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [TestCatalogs.Apple]);

    private static Principal National(string nationality) => PrincipalWith(("nationality", [nationality]));

    [Fact]
    public void Check_PassesWhenEveryGatePasses()
    {
        var principal = National("UK");

        Assert.True(MarkingGate.Check(Everything, principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)).IsAllowed);
        Assert.All(MarkingGate.CheckAll(Everything, principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)), c => Assert.True(c.Passed));
    }

    [Fact]
    public void Order_IsGrantThenCaveat()
    {
        // Both gates fail; knock them down one at a time from the front and the reported
        // reason must move to the next gate in G, N order.
        var failsGn = National("US");
        Assert.Equal("selector:not_granted:FRUIT", MarkingGate.Check(Everything, failsGn, TestCatalogs.Fruit, Nothing).DenialReason);

        var failsN = National("US");
        Assert.Equal("caveat:eyes_only", MarkingGate.Check(Everything, failsN, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)).DenialReason);
    }

    [Fact]
    public void CheckAll_ListsEveryFailure()
    {
        var failsAll = National("US");

        var checks = MarkingGate.CheckAll(Everything, failsAll, TestCatalogs.Fruit, Nothing);

        Assert.Equal([GateKind.SelectorGrant, GateKind.NationalCaveat], checks.Select(c => c.Kind));
        Assert.All(checks, c => Assert.False(c.Passed));
        Assert.Equal(["selector:not_granted:FRUIT", "caveat:eyes_only"], checks.Select(c => c.Reason));
    }

    [Fact]
    public void CheckAll_FirstFailure_IsExactlyWhatCheckReports()
    {
        // The explanation can never disagree with the gate: over every combination of
        // the principal facts, the first failed entry in CheckAll carries the same reason
        // Check returns, and CheckAll is all-passed exactly when Check allows.
        foreach (var nationality in new[] { "UK", "US" })
        foreach (var granted in new[] { Nothing, Granted(TestCatalogs.Apple) })
        foreach (var marking in new[] { Everything, ProtectiveMarking.FailClosed, ProtectiveMarking.Baseline })
        {
            var principal = National(nationality);
            var verdict = MarkingGate.Check(marking, principal, TestCatalogs.Fruit, granted);
            var checks = MarkingGate.CheckAll(marking, principal, TestCatalogs.Fruit, granted);

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
        foreach (var principal in new[] { National("US"), National("UK"), PrincipalWith() })
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
    public void Check_IgnoresTheLevel()
    {
        // The level joined the prefix outside the gate (§21.12) when this deployment
        // dropped the clearance attribute: over every level, the same selectors, caveat,
        // principal and grant give the same verdict and the same reason. A real TOP SECRET
        // page is readable by whoever its selectors and caveat admit.
        foreach (var selectors in new[] { Array.Empty<SelectorValue>(), [TestCatalogs.Apple] })
        foreach (string[] countries in new[] { Array.Empty<string>(), ["UK"] })
        foreach (var principal in new[] { National("US"), National("UK"), PrincipalWith() })
        foreach (var granted in new[] { Nothing, Granted(TestCatalogs.Apple) })
        {
            var verdicts = Enum.GetValues<ClassificationLevel>()
                .Select(level => MarkingGate.Check(ProtectiveMarking.Create(level, countries, selectors), principal, TestCatalogs.Fruit, granted))
                .ToList();

            Assert.Single(verdicts.Select(v => v.IsAllowed).Distinct());
            Assert.Single(verdicts.Select(v => v.DenialReason).Distinct());
        }

        Assert.True(MarkingGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.TopSecret, null), PrincipalWith(), SelectorCatalog.Empty, Nothing).IsAllowed);
    }

    [Fact]
    public void NoSelectors_ReducesToTheCaveatGate()
    {
        // A marking without selectors goes through exactly N - byte-identical to
        // CaveatGate.Check, which is what lets the composed gate replace it everywhere.
        var plain = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        foreach (var principal in new[] { National("US"), National("UK"), PrincipalWith() })
        {
            var composed = MarkingGate.Check(plain, principal, SelectorCatalog.Empty, Nothing);
            var caveat = CaveatGate.Check(plain, principal);

            Assert.Equal(caveat.IsAllowed, composed.IsAllowed);
            Assert.Equal(caveat.DenialReason, composed.DenialReason);
        }
    }

    // --- The missing-row trap (design.md §21) --------------------------------------------

    [Fact]
    public void AnUnavailableMarking_DeniesEveryone_BeforeAnyOtherGate()
    {
        // FailClosed carries no selectors and no caveat, and its level is not compared
        // against anything any more - so without a gate of its own it would admit
        // everybody with space access. It has one, and it is first: the most generous
        // principal and the most generous grant are refused with the availability token.
        var everyone = new[] { PrincipalWith(), National("UK"), National("US") };
        var grants = new[] { Nothing, Granted(TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South) };

        foreach (var principal in everyone)
        foreach (var granted in grants)
        foreach (var catalog in new[] { TestCatalogs.Fruit, SelectorCatalog.Empty })
        {
            var verdict = MarkingGate.Check(ProtectiveMarking.FailClosed, principal, catalog, granted);
            Assert.False(verdict.IsAllowed);
            Assert.Equal("marking:unavailable", verdict.DenialReason);

            var only = Assert.Single(MarkingGate.CheckAll(ProtectiveMarking.FailClosed, principal, catalog, granted));
            Assert.Equal(GateKind.MarkingUnavailable, only.Kind);
            Assert.False(only.Passed);
        }
    }

    [Fact]
    public void ARealTopSecretMarking_IsNotUnavailable_AndIsReadable()
    {
        // The flag is set by the FailClosed factory alone, never inferred from the level:
        // a page somebody legitimately marked TOP SECRET, with nothing else on it, is
        // readable by everyone with space access.
        var real = ProtectiveMarking.Create(ClassificationLevel.TopSecret, null, null, prefix: null);

        Assert.False(real.IsUnavailable);
        Assert.True(ProtectiveMarking.FailClosed.IsUnavailable);
        Assert.NotEqual(ProtectiveMarking.FailClosed, real); // same level, same (no) prefix - different markings
        Assert.True(MarkingGate.Check(real, PrincipalWith(), SelectorCatalog.Empty, Nothing).IsAllowed);
    }
}
