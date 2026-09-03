using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.15: the two selector gates in isolation — eligibility (E: the instance
/// knows the category and the principal's claim says yes) and grant (G: a matching access
/// grant confers the value). These pin every fail-closed branch, the E-before-G order, and
/// that no reason ever names a selector VALUE.
/// </summary>
public class SelectorGateTests
{
    private static readonly IReadOnlySet<SelectorValue> Nothing = new HashSet<SelectorValue>();

    private static IReadOnlySet<SelectorValue> Granted(params SelectorValue[] values) => new HashSet<SelectorValue>(values);

    private static Principal PrincipalWith(params (string Key, string[] Values)[] attributes) =>
        Principal.Create(
            "user-sub",
            [],
            attributes.Select(a => new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values)));

    private static ProtectiveMarking Marked(params SelectorValue[] selectors) =>
        ProtectiveMarking.Create(ClassificationLevel.Official, null, selectors);

    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData(" Yes ")]
    public void Eligible_WhenClaimSaysYes_TrimmedCaseInsensitive(string claimValue)
    {
        // The documented departure from §6.3's ordinal rule, confined to marking
        // comparisons: the claim value is set by a realm mapper, and failing closed on
        // "Yes" versus "yes" would be an outage dressed as security.
        var principal = PrincipalWith((TestCatalogs.FruitClaim, [claimValue]));

        var result = SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple));

        Assert.True(result.IsAllowed);
        Assert.Contains("FRUIT", SelectorGate.ResolveEligibleCategories(principal, TestCatalogs.Fruit));
    }

    [Fact]
    public void NotEligible_WithoutTheClaim_FailsClosed()
    {
        // No fruit attribute at all: like Attr_MissingAttribute_FailsClosed, a principal
        // with no value for an attribute matches no condition that tests it.
        var principal = PrincipalWith();

        var result = SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple));

        Assert.False(result.IsAllowed);
        Assert.Equal("selector:not_eligible:FRUIT", result.DenialReason);
        Assert.DoesNotContain("FRUIT", SelectorGate.ResolveEligibleCategories(principal, TestCatalogs.Fruit));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("true")]
    [InlineData("y")]
    [InlineData("yes please")]
    public void NotEligible_WhenClaimSaysAnythingButYes(string claimValue)
    {
        var principal = PrincipalWith((TestCatalogs.FruitClaim, [claimValue]));

        var result = SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple));

        Assert.False(result.IsAllowed);
        Assert.Equal("selector:not_eligible:FRUIT", result.DenialReason);
    }

    [Fact]
    public void MultiValuedClaim_IsEligibleIfAnyValueSaysYes()
    {
        var principal = PrincipalWith((TestCatalogs.FruitClaim, ["no", "yes"]));

        Assert.True(SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Granted(TestCatalogs.Apple)).IsAllowed);
    }

    [Fact]
    public void ClaimlessCategory_IsEligibleToEveryone()
    {
        // REGION gates nobody: eligibility is the grant alone.
        var principal = PrincipalWith();

        Assert.True(SelectorGate.Check(Marked(TestCatalogs.North), principal, TestCatalogs.Fruit, Granted(TestCatalogs.North)).IsAllowed);
        Assert.Contains("REGION", SelectorGate.ResolveEligibleCategories(principal, TestCatalogs.Fruit));
    }

    [Fact]
    public void UnknownCategory_IsEligibleToNobody_WithTheUnknownReason()
    {
        // The §12 posture: a bundle from an instance that configured COLOUR lands here,
        // where nobody configured it, and matches nobody - with its OWN token so an
        // operator can tell "not configured" from "not cleared".
        var colour = new SelectorValue("COLOUR", "RED");
        var principal = PrincipalWith((TestCatalogs.FruitClaim, ["yes"]), ("colour", ["yes"]));

        var result = SelectorGate.Check(Marked(colour), principal, TestCatalogs.Fruit, Granted(colour));

        Assert.False(result.IsAllowed);
        Assert.Equal("selector:unknown:COLOUR", result.DenialReason);
        // The empty catalog makes EVERY category unknown - the fail-closed default for an
        // unconfigured instance.
        Assert.Equal(
            "selector:unknown:FRUIT",
            SelectorGate.Check(Marked(TestCatalogs.Apple), principal, SelectorCatalog.Empty, Granted(TestCatalogs.Apple)).DenialReason);
        Assert.DoesNotContain("COLOUR", SelectorGate.ResolveEligibleCategories(principal, TestCatalogs.Fruit));
    }

    [Fact]
    public void NotGranted_WhenNoMatchingGrantCarriesTheValue()
    {
        var principal = PrincipalWith((TestCatalogs.FruitClaim, ["yes"]));

        var nothing = SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Nothing);
        var wrongValue = SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Granted(TestCatalogs.Banana));

        Assert.False(nothing.IsAllowed);
        Assert.Equal("selector:not_granted:FRUIT", nothing.DenialReason);
        Assert.False(wrongValue.IsAllowed);
        Assert.Equal("selector:not_granted:FRUIT", wrongValue.DenialReason);
    }

    [Fact]
    public void Granted_ByAnyMatchingGrant_UnionSemantics()
    {
        // The granted set is the union the calculator builds over every matching access
        // grant; the gate only asks whether the value is in it.
        var principal = PrincipalWith((TestCatalogs.FruitClaim, ["yes"]));
        var union = Granted(TestCatalogs.Banana, TestCatalogs.Apple, TestCatalogs.South);

        Assert.True(SelectorGate.Check(Marked(TestCatalogs.Apple, TestCatalogs.South), principal, TestCatalogs.Fruit, union).IsAllowed);
    }

    [Fact]
    public void Reason_NamesTheCategoryNeverTheValue()
    {
        // §15: the category is bounded configured vocabulary; the value is the marking's
        // content and travels only in the structured GateCheck.
        var principal = PrincipalWith();
        var sentinel = new SelectorValue("FRUIT", "ZZSENTINELZZ");

        var notEligible = SelectorGate.Check(Marked(sentinel), principal, TestCatalogs.Fruit, Granted(sentinel));
        var notGranted = SelectorGate.Check(
            Marked(sentinel), PrincipalWith((TestCatalogs.FruitClaim, ["yes"])), TestCatalogs.Fruit, Nothing);
        var unknown = SelectorGate.Check(
            Marked(new SelectorValue("ZZCAT", "ZZSENTINELZZ")), principal, TestCatalogs.Fruit, Nothing);

        foreach (var result in new[] { notEligible, notGranted, unknown })
        {
            Assert.False(result.IsAllowed);
            Assert.DoesNotContain("ZZSENTINELZZ", result.DenialReason!, StringComparison.Ordinal);
        }

        // ...but the structured check carries it, for the inspector.
        var check = Assert.Single(SelectorGate.CheckAll(Marked(sentinel), principal, TestCatalogs.Fruit, Granted(sentinel)), c => !c.Passed);
        Assert.Equal("FRUIT", check.SelectorCategory);
        Assert.Equal("ZZSENTINELZZ", check.SelectorValue);
    }

    [Fact]
    public void EligibilityIsCheckedBeforeGrant_OneReasonPerSelector()
    {
        // Fails both: not eligible AND not granted. The coarser fact is reported, and the
        // principal is never told what the space would have granted them.
        var principal = PrincipalWith();

        var result = SelectorGate.Check(Marked(TestCatalogs.Apple), principal, TestCatalogs.Fruit, Nothing);

        Assert.Equal("selector:not_eligible:FRUIT", result.DenialReason);
    }

    [Fact]
    public void AllEligibilityIsReportedBeforeAnyGrant_AcrossSelectors()
    {
        // FRUIT fails only G (eligible, not granted); REGION fails E (unknown here). The
        // ladder is all E, then all G, so REGION's eligibility failure wins even though
        // FRUIT sorts first.
        var catalog = SelectorCatalog.Create([new SelectorCategory("FRUIT", null, TestCatalogs.FruitClaim, ["APPLE"])]);
        var principal = PrincipalWith((TestCatalogs.FruitClaim, ["yes"]));

        var result = SelectorGate.Check(Marked(TestCatalogs.Apple, TestCatalogs.North), principal, catalog, Nothing);

        Assert.Equal("selector:unknown:REGION", result.DenialReason);
    }

    [Fact]
    public void CheckAll_ListsEveryEThenEveryG_EvaluatedIndependently()
    {
        var principal = PrincipalWith(); // not eligible for FRUIT; eligible for REGION (claimless)

        var checks = SelectorGate.CheckAll(
            Marked(TestCatalogs.Apple, TestCatalogs.North), principal, TestCatalogs.Fruit, Granted(TestCatalogs.North));

        Assert.Equal(
            [GateKind.SelectorEligibility, GateKind.SelectorEligibility, GateKind.SelectorGrant, GateKind.SelectorGrant],
            checks.Select(c => c.Kind));
        Assert.Equal(["FRUIT", "REGION", "FRUIT", "REGION"], checks.Select(c => c.SelectorCategory));
        Assert.Equal([false, true, false, true], checks.Select(c => c.Passed));
        Assert.Equal("selector:not_eligible:FRUIT", checks[0].Reason);
        Assert.Equal("selector:not_granted:FRUIT", checks[2].Reason);
        Assert.Null(checks[1].Reason);
    }

    [Fact]
    public void NoSelectors_PassesWithNothingToList()
    {
        var principal = PrincipalWith();
        var plain = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);

        Assert.True(SelectorGate.Check(plain, principal, SelectorCatalog.Empty, Nothing).IsAllowed);
        Assert.Empty(SelectorGate.CheckAll(plain, principal, SelectorCatalog.Empty, Nothing));
    }
}
