using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.15: the selector gate in isolation — grant (G: a matching access grant
/// confers the value), with the unknown-category branch reported distinctly. These pin
/// every fail-closed branch, the canonical order, and that no reason ever names a
/// selector VALUE.
///
/// <para>The eligibility gate (E: a per-category Keycloak claim saying <c>yes</c>) used
/// to be tested here too, and its cases are gone with it — this deployment carries no
/// such claims. What remains is the whole of what decides a selector: the space's grant.</para>
/// </summary>
public class SelectorGateTests
{
    private static readonly IReadOnlySet<SelectorValue> Nothing = new HashSet<SelectorValue>();

    private static IReadOnlySet<SelectorValue> Granted(params SelectorValue[] values) => new HashSet<SelectorValue>(values);

    private static ProtectiveMarking Marked(params SelectorValue[] selectors) =>
        ProtectiveMarking.Create(ClassificationLevel.Official, null, selectors);

    [Fact]
    public void Granted_WhenAMatchingGrantCarriesTheValue()
    {
        Assert.True(SelectorGate.Check(Marked(TestCatalogs.Apple), TestCatalogs.Fruit, Granted(TestCatalogs.Apple)).IsAllowed);
        Assert.True(SelectorGate.Check(Marked(TestCatalogs.North), TestCatalogs.Fruit, Granted(TestCatalogs.North)).IsAllowed);
    }

    [Fact]
    public void NotGranted_WhenNoMatchingGrantCarriesTheValue()
    {
        var nothing = SelectorGate.Check(Marked(TestCatalogs.Apple), TestCatalogs.Fruit, Nothing);
        var wrongValue = SelectorGate.Check(Marked(TestCatalogs.Apple), TestCatalogs.Fruit, Granted(TestCatalogs.Banana));

        Assert.False(nothing.IsAllowed);
        Assert.Equal("selector:not_granted:FRUIT", nothing.DenialReason);
        Assert.False(wrongValue.IsAllowed);
        Assert.Equal("selector:not_granted:FRUIT", wrongValue.DenialReason);
    }

    [Fact]
    public void ThereIsNoClaimThatGrants_OnlyTheSpaceDoes()
    {
        // The test that would have passed under the old eligibility gate for the wrong
        // reason: a principal holding every conceivable attribute is still refused a
        // selector no grant in this space confers. Nothing about the principal reaches
        // this gate at all - its whole input is the marking and the granted union.
        var result = SelectorGate.Check(Marked(TestCatalogs.Apple), TestCatalogs.Fruit, Nothing);

        Assert.False(result.IsAllowed);
        Assert.Equal("selector:not_granted:FRUIT", result.DenialReason);
    }

    [Fact]
    public void UnknownCategory_IsGrantedToNobody_WithTheUnknownReason()
    {
        // The §12 posture: a bundle from an instance that configured COLOUR lands here,
        // where nobody configured it, and matches nobody - with its OWN token so an
        // operator can tell "not configured" from "not granted". Even a granted union
        // that somehow carries the value (unrepresentable through the grant writers,
        // which refuse a value the catalog does not know) does not admit: the branch is
        // decided before the union is consulted.
        var colour = new SelectorValue("COLOUR", "RED");

        var result = SelectorGate.Check(Marked(colour), TestCatalogs.Fruit, Granted(colour));

        Assert.False(result.IsAllowed);
        Assert.Equal("selector:unknown:COLOUR", result.DenialReason);
        // The empty catalog makes EVERY category unknown - the fail-closed default for an
        // unconfigured instance.
        Assert.Equal(
            "selector:unknown:FRUIT",
            SelectorGate.Check(Marked(TestCatalogs.Apple), SelectorCatalog.Empty, Granted(TestCatalogs.Apple)).DenialReason);
    }

    [Fact]
    public void Granted_ByAnyMatchingGrant_UnionSemantics()
    {
        // The granted set is the union the calculator builds over every matching access
        // grant; the gate only asks whether the value is in it.
        var union = Granted(TestCatalogs.Banana, TestCatalogs.Apple, TestCatalogs.South);

        Assert.True(SelectorGate.Check(Marked(TestCatalogs.Apple, TestCatalogs.South), TestCatalogs.Fruit, union).IsAllowed);
    }

    [Fact]
    public void Reason_NamesTheCategoryNeverTheValue()
    {
        // §15: the category is bounded configured vocabulary; the value is the marking's
        // content and travels only in the structured GateCheck.
        var sentinel = new SelectorValue("FRUIT", "ZZSENTINELZZ");

        var notGranted = SelectorGate.Check(Marked(sentinel), TestCatalogs.Fruit, Nothing);
        var unknown = SelectorGate.Check(Marked(new SelectorValue("ZZCAT", "ZZSENTINELZZ")), TestCatalogs.Fruit, Nothing);

        foreach (var result in new[] { notGranted, unknown })
        {
            Assert.False(result.IsAllowed);
            Assert.DoesNotContain("ZZSENTINELZZ", result.DenialReason!, StringComparison.Ordinal);
        }

        // ...but the structured check carries it, for the inspector.
        var check = Assert.Single(SelectorGate.CheckAll(Marked(sentinel), TestCatalogs.Fruit, Nothing), c => !c.Passed);
        Assert.Equal("FRUIT", check.SelectorCategory);
        Assert.Equal("ZZSENTINELZZ", check.SelectorValue);
    }

    [Fact]
    public void SelectorsAreReportedInCanonicalOrder_OneReasonPerMarking()
    {
        // FRUIT fails G (not granted); REGION is unknown to this one-category catalog.
        // Canonical (ordinal) category order decides which is named: FRUIT sorts first,
        // so its grant failure wins even though REGION's failure is the coarser fact.
        var catalog = SelectorCatalog.Create([new SelectorCategory("FRUIT", null, ["APPLE"])]);

        var result = SelectorGate.Check(Marked(TestCatalogs.Apple, TestCatalogs.North), catalog, Nothing);

        Assert.Equal("selector:not_granted:FRUIT", result.DenialReason);
        Assert.Equal(
            "selector:unknown:REGION",
            SelectorGate.Check(Marked(TestCatalogs.Apple, TestCatalogs.North), catalog, Granted(TestCatalogs.Apple)).DenialReason);
    }

    [Fact]
    public void CheckAll_ListsEveryG_EvaluatedIndependently()
    {
        var checks = SelectorGate.CheckAll(
            Marked(TestCatalogs.Apple, TestCatalogs.North), TestCatalogs.Fruit, Granted(TestCatalogs.North));

        Assert.Equal([GateKind.SelectorGrant, GateKind.SelectorGrant], checks.Select(c => c.Kind));
        Assert.Equal(["FRUIT", "REGION"], checks.Select(c => c.SelectorCategory));
        Assert.Equal([false, true], checks.Select(c => c.Passed));
        Assert.Equal("selector:not_granted:FRUIT", checks[0].Reason);
        Assert.Null(checks[1].Reason);
    }

    [Fact]
    public void Check_StopsAtTheFirstFailure_CheckAllDoesNot()
    {
        var stopped = SelectorGate.Evaluate(Marked(TestCatalogs.Apple, TestCatalogs.North), TestCatalogs.Fruit, Nothing, shortCircuit: true);
        var full = SelectorGate.Evaluate(Marked(TestCatalogs.Apple, TestCatalogs.North), TestCatalogs.Fruit, Nothing, shortCircuit: false);

        Assert.Single(stopped);
        Assert.Equal(2, full.Count);
        Assert.Equal(stopped[0].Reason, full[0].Reason);
    }

    [Fact]
    public void NoSelectors_PassesWithNothingToList()
    {
        var plain = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);

        Assert.True(SelectorGate.Check(plain, SelectorCatalog.Empty, Nothing).IsAllowed);
        Assert.Empty(SelectorGate.CheckAll(plain, SelectorCatalog.Empty, Nothing));
    }
}
