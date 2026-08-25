using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21: the classification decision itself, in isolation from the permission
/// computation that AND-s it onto canView. These are the tests that pin the fail-closed
/// choices — what an absent, garbage, or multi-valued clearance claim is worth, and what
/// an eyes-only caveat does with a principal who has no nationality.
/// </summary>
public class ClearanceGateTests
{
    private static Principal PrincipalWith(params (string Key, string[] Values)[] attributes) =>
        Principal.Create(
            "user-sub",
            [],
            attributes.Select(a => new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values)));

    private static Principal Cleared(ClassificationLevel level, params string[] nationalities) =>
        nationalities.Length == 0
            ? PrincipalWith(("clearance", [ProtectiveMarking.LevelWireName(level)]))
            : PrincipalWith(("clearance", [ProtectiveMarking.LevelWireName(level)]), ("nationality", nationalities));

    public static TheoryData<ClassificationLevel, ClassificationLevel, bool> Ladder()
    {
        // Exhaustive over the whole 4x4 scheme rather than a few spot checks: the
        // ordering IS the control, and a spot check would not notice a member inserted
        // in the middle of the enum re-ranking everything below it.
        var data = new TheoryData<ClassificationLevel, ClassificationLevel, bool>();
        ClassificationLevel[] levels =
        [
            ClassificationLevel.Official,
            ClassificationLevel.OfficialSensitive,
            ClassificationLevel.Secret,
            ClassificationLevel.TopSecret,
        ];

        foreach (var clearance in levels)
        {
            foreach (var marking in levels)
            {
                data.Add(clearance, marking, clearance >= marking);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Ladder))]
    public void Check_AdmitsExactlyTheLevelsAtOrBelowTheClearance(
        ClassificationLevel clearance, ClassificationLevel markingLevel, bool expectedAllowed)
    {
        var result = ClearanceGate.Check(
            ProtectiveMarking.Create(markingLevel, null), Cleared(clearance));

        Assert.Equal(expectedAllowed, result.IsAllowed);
        if (!expectedAllowed)
        {
            Assert.Equal($"classification:{ProtectiveMarking.LevelToken(markingLevel)}", result.DenialReason);
        }
    }

    [Fact]
    public void ResolveClearance_AbsentAttribute_IsOfficial_NotNothingAndNotEverything()
    {
        // §21's deliberate middle. Not "see everything" (obviously wrong) and not "see
        // nothing" (an outage that pressures someone into disabling the control).
        Assert.Equal(ClassificationLevel.Official, ClearanceGate.ResolveClearance(PrincipalWith()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TopSecret")]      // the C# member spelling is NOT the claim vocabulary
    [InlineData("top_secret")]     // §6.3: matching is ordinal, no case folding
    [InlineData("TOP SECRET")]     // the human marking is not the machine name either
    [InlineData("4")]              // Enum.TryParse would have accepted this as TopSecret
    [InlineData("SUPER_SECRET")]
    public void ResolveClearance_UnrecognisedValue_IsOfficial(string value)
    {
        Assert.Equal(ClassificationLevel.Official, ClearanceGate.ResolveClearance(PrincipalWith(("clearance", [value]))));
    }

    [Fact]
    public void ResolveClearance_GarbageClearance_StillSeesOfficial_ButNothingAbove()
    {
        var principal = PrincipalWith(("clearance", ["nonsense"]));

        Assert.True(ClearanceGate.Check(ProtectiveMarking.Baseline, principal).IsAllowed);
        Assert.False(ClearanceGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.OfficialSensitive, null), principal).IsAllowed);
    }

    [Fact]
    public void ResolveClearance_MultiValued_TakesTheHighestRecognised_AndIgnoresGarbage()
    {
        // Mirrors §6.4's "your role is the highest whose expression you satisfy". Garbage
        // alongside a real value must neither raise nor lower the answer.
        var principal = PrincipalWith(("clearance", ["OFFICIAL", "nonsense", "SECRET", "17"]));

        Assert.Equal(ClassificationLevel.Secret, ClearanceGate.ResolveClearance(principal));
    }

    // --- Eyes-only caveat ---------------------------------------------------------------

    [Fact]
    public void EyesOnly_SingleCountry_AdmitsAMatchingNationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "GB")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_MultipleCountries_AdmitAnyOneOfThem()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB", "US"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "US")).IsAllowed);
        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "GB")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_DualNational_MatchesOnEitherNationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "GB", "US")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_NoOverlap_IsDenied_WithTheCaveatReason_NamingNoCountry()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB", "US"]);

        var result = ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "NZ"));

        Assert.False(result.IsAllowed);
        // §15: the reason is a bounded token. The countries live in the audit row only -
        // a reason string that named them would be one careless tag away from a metric
        // dimension carrying the marking's contents.
        Assert.Equal("caveat:eyes_only", result.DenialReason);
        Assert.DoesNotContain("GB", result.DenialReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void EyesOnly_AbsentNationalityAttribute_IsDenied_FailingClosedLikeAttrCondition()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]);
        var noNationality = PrincipalWith(("clearance", ["SECRET"]));

        var result = ClearanceGate.Check(marking, noNationality);

        Assert.False(result.IsAllowed);
        Assert.Equal("caveat:eyes_only", result.DenialReason);
    }

    [Fact]
    public void EyesOnly_EmptyNationalityValues_IsDenied()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]);
        var blank = PrincipalWith(("clearance", ["SECRET"]), ("nationality", ["", "  "]));

        Assert.False(ClearanceGate.Check(marking, blank).IsAllowed);
    }

    [Fact]
    public void EyesOnly_EmptySet_IsNoCaveatAtAll_EvenWithoutANationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, []);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret)).IsAllowed);
    }

    [Theory]
    [InlineData("gb")]
    [InlineData("Gb")]
    [InlineData(" GB ")]
    public void EyesOnly_ComparisonIsNormalizedOnBothSides(string heldNationality)
    {
        // §21's documented, deliberate departure from §6.3's ordinal-no-folding rule,
        // confined to this comparison: the two sides come from different systems (an
        // admin-registered vocabulary and an OIDC claim mapper) that were never
        // guaranteed to agree on case, and a casing mismatch here would deny every
        // legitimate reader while looking correct.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["gb"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, heldNationality)).IsAllowed);
    }

    [Fact]
    public void LevelIsCheckedBeforeTheCaveat_SoOneDenialNamesOneReason()
    {
        // Failing both must report the coarser fact. Stable reasons are what make §7's
        // audit rows and the §6.6 inspector agree.
        var marking = ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["GB"]);

        var result = ClearanceGate.Check(marking, Cleared(ClassificationLevel.Official, "NZ"));

        Assert.Equal("classification:top_secret", result.DenialReason);
    }
}
