using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21: the classification and caveat decisions themselves, in isolation from
/// the permission computation that AND-s them onto canView. These are the tests that pin
/// the fail-closed choices — what an absent, garbage, or multi-valued clearance claim is
/// worth, what a nationality outside the fixed set is worth, and what an eyes-only caveat
/// does with a principal who has no nationality.
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
            ProtectiveMarking.Create(markingLevel, null, prefix: null), Cleared(clearance));

        Assert.Equal(expectedAllowed, result.IsAllowed);
        if (!expectedAllowed)
        {
            Assert.Equal($"classification:{ProtectiveMarking.LevelToken(markingLevel)}", result.DenialReason);
        }
    }

    [Fact]
    public void DefaultClearance_IsOfficialSensitive()
    {
        // The one constant the resolver and the `me` query's anonymous floor both read.
        Assert.Equal(ClassificationLevel.OfficialSensitive, ClearanceGate.DefaultClearance);
    }

    [Fact]
    public void ResolveClearance_AbsentAttribute_IsOfficialSensitive_NotNothingAndNotEverything()
    {
        // §21.3's deliberate middle. Not "see everything" (obviously wrong) and not "see
        // nothing" (an outage that pressures someone into disabling the control).
        // OFFICIAL-SENSITIVE is the everyday working tier, so it is what "nothing" is
        // worth; SECRET and above stay denied.
        Assert.Equal(ClassificationLevel.OfficialSensitive, ClearanceGate.ResolveClearance(PrincipalWith()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TopSecret")]      // the C# member spelling is NOT the claim vocabulary
    [InlineData("top_secret")]     // §6.3: matching is ordinal, no case folding
    [InlineData("TOP SECRET")]     // the human marking is not the machine name either
    [InlineData("4")]              // Enum.TryParse would have accepted this as TopSecret
    [InlineData("SUPER_SECRET")]
    public void ResolveClearance_UnrecognisedValue_IsOfficialSensitive(string value)
    {
        Assert.Equal(ClassificationLevel.OfficialSensitive, ClearanceGate.ResolveClearance(PrincipalWith(("clearance", [value]))));
    }

    [Fact]
    public void ResolveClearance_AnExplicitOfficialClaim_IsBelowTheFloor_AndIsHonoured()
    {
        // The floor is what an ABSENT or GARBAGE claim is worth. A realm that says
        // OFFICIAL means OFFICIAL, and the gate must not silently promote it: a stated
        // clearance is a fact the token asserted.
        var principal = PrincipalWith(("clearance", ["OFFICIAL"]));

        Assert.Equal(ClassificationLevel.Official, ClearanceGate.ResolveClearance(principal));
        Assert.False(ClearanceGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.OfficialSensitive, null), principal).IsAllowed);
    }

    [Fact]
    public void ResolveClearance_GarbageClearance_StillSeesOfficialSensitive_ButNothingAbove()
    {
        var principal = PrincipalWith(("clearance", ["nonsense"]));

        Assert.True(ClearanceGate.Check(ProtectiveMarking.Baseline, principal).IsAllowed);
        Assert.True(ClearanceGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.OfficialSensitive, null), principal).IsAllowed);
        Assert.False(ClearanceGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.Secret, null), principal).IsAllowed);
    }

    [Fact]
    public void ResolveClearance_MultiValued_TakesTheHighestRecognised_AndIgnoresGarbage()
    {
        // Mirrors §6.4's "your role is the highest whose expression you satisfy". Garbage
        // alongside a real value must neither raise nor lower the answer.
        var principal = PrincipalWith(("clearance", ["OFFICIAL", "nonsense", "SECRET", "17"]));

        Assert.Equal(ClassificationLevel.Secret, ClearanceGate.ResolveClearance(principal));
    }

    [Fact]
    public void ResolveClearance_MultiValuedGarbageOnly_IsTheFloor()
    {
        Assert.Equal(
            ClassificationLevel.OfficialSensitive,
            ClearanceGate.ResolveClearance(PrincipalWith(("clearance", ["nonsense", "17"]))));
    }

    // --- Nationality resolution (design.md §21.4) ------------------------------------------

    [Fact]
    public void ResolveNationalities_IgnoresValuesOutsideTheFixedSet_SoAMapperEmittingGbHoldsNothing()
    {
        // The reversed §21.4: one hard-coded vocabulary on both sides, so the only failure
        // left is a mapper emitting a foreign token - and that becomes an EMPTY nationality
        // (visible in me.nationality), never a silent partial match.
        var held = ClearanceGate.ResolveNationalities(PrincipalWith(("nationality", ["GB", "gbr", " nz ", "FR"])));

        Assert.Equal(["NZ"], held);
        Assert.Empty(ClearanceGate.ResolveNationalities(PrincipalWith(("nationality", ["GB"]))));
        Assert.Empty(ClearanceGate.ResolveNationalities(PrincipalWith()));
    }

    // --- Eyes-only caveat ---------------------------------------------------------------

    [Fact]
    public void EyesOnly_SingleCountry_AdmitsAMatchingNationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "UK")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_MultipleCountries_AdmitAnyOneOfThem()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "US")).IsAllowed);
        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "UK")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_DualNational_MatchesOnEitherNationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "UK", "US")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_NoOverlap_IsDenied_WithTheCaveatReason_NamingNoCountry()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"]);

        var result = ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, "NZ"));

        Assert.False(result.IsAllowed);
        // §15: the reason is a bounded token. The countries live in the audit row only -
        // a reason string that named them would be one careless tag away from a metric
        // dimension carrying the marking's contents.
        Assert.Equal("caveat:eyes_only", result.DenialReason);
        Assert.DoesNotContain("UK", result.DenialReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void EyesOnly_AbsentNationalityAttribute_IsDenied_FailingClosedLikeAttrCondition()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        var noNationality = PrincipalWith(("clearance", ["SECRET"]));

        var result = ClearanceGate.Check(marking, noNationality);

        Assert.False(result.IsAllowed);
        Assert.Equal("caveat:eyes_only", result.DenialReason);
    }

    [Fact]
    public void EyesOnly_EmptyNationalityValues_IsDenied()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        var blank = PrincipalWith(("clearance", ["SECRET"]), ("nationality", ["", "  "]));

        Assert.False(ClearanceGate.Check(marking, blank).IsAllowed);
    }

    [Fact]
    public void EyesOnly_ALegacyGbToken_OnEitherSide_MatchesNobody()
    {
        // A GB row (pre-migration data, or an older bundle) is kept verbatim on the
        // marking and dropped on the principal side, so it can never match - fail closed
        // rather than a display-only alias to UK that enforces something else.
        var legacyRow = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]);

        Assert.False(ClearanceGate.Check(legacyRow, Cleared(ClassificationLevel.Secret, "GB")).IsAllowed);
        Assert.False(ClearanceGate.Check(legacyRow, Cleared(ClassificationLevel.Secret, "UK")).IsAllowed);
        Assert.False(ClearanceGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]), Cleared(ClassificationLevel.Secret, "GB")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_EmptySet_IsNoCaveatAtAll_EvenWithoutANationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, []);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret)).IsAllowed);
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("Uk")]
    [InlineData(" UK ")]
    public void EyesOnly_ComparisonIsNormalizedOnBothSides(string heldNationality)
    {
        // §21's documented, deliberate departure from §6.3's ordinal-no-folding rule,
        // confined to this comparison: the two sides come from different systems (a
        // fixed vocabulary and an OIDC claim mapper) that were never guaranteed to agree
        // on case, and a casing mismatch here would deny every legitimate reader while
        // looking correct.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["uk"]);

        Assert.True(ClearanceGate.Check(marking, Cleared(ClassificationLevel.Secret, heldNationality)).IsAllowed);
    }

    // --- The prefix is outside the gate (design.md §21.12) -------------------------------

    public static TheoryData<string?> Prefixes() => new() { null, "", "UK", "NATO", "ZZNONSENSEZZ" };

    [Theory]
    [MemberData(nameof(Prefixes))]
    public void Check_IgnoresTheNationalPrefixEntirely_ForEveryLevelCaveatAndSelectorCombination(string? prefix)
    {
        // THE test that pins "the prefix has no access-control considerations
        // whatsoever". Every level, with and without a caveat, with and without a
        // selector, against a principal who passes and one who does not - and the verdict
        // AND the denial reason must be identical to the no-prefix marking in every single
        // cell. If someone ever threads the prefix into the gate "for completeness", this fails.
        foreach (var level in Enum.GetValues<ClassificationLevel>())
        {
            foreach (string[] countries in new[] { Array.Empty<string>(), ["UK"], ["UK", "US"] })
            foreach (SelectorValue[] selectors in new[] { Array.Empty<SelectorValue>(), [TestCatalogs.Apple] })
            {
                var baseline = ProtectiveMarking.Create(level, countries, selectors, prefix: null);
                var prefixed = ProtectiveMarking.Create(level, countries, selectors, prefix);

                foreach (var principal in new[]
                {
                    PrincipalWith(),
                    Cleared(ClassificationLevel.Official, "UK"),
                    Cleared(ClassificationLevel.Secret, "NZ"),
                    Cleared(ClassificationLevel.TopSecret, "UK", "US"),
                })
                {
                    var without = ClearanceGate.Check(baseline, principal);
                    var with = ClearanceGate.Check(prefixed, principal);

                    Assert.Equal(without.IsAllowed, with.IsAllowed);
                    // Reasons too: a prefix must not leak into the audit vocabulary any
                    // more than into the verdict.
                    Assert.Equal(without.DenialReason, with.DenialReason);
                }
            }
        }
    }

    [Fact]
    public void ADenialReason_NeverMentionsThePrefix()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["UK"], prefix: "ZZPREFIXSENTINELZZ");

        var levelFailure = ClearanceGate.Check(marking, Cleared(ClassificationLevel.Official, "UK"));
        var caveatFailure = ClearanceGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.Official, ["UK"], prefix: "ZZPREFIXSENTINELZZ"),
            Cleared(ClassificationLevel.Official, "NZ"));

        Assert.DoesNotContain("ZZPREFIXSENTINELZZ", levelFailure.DenialReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZZPREFIXSENTINELZZ", caveatFailure.DenialReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("classification:top_secret", levelFailure.DenialReason);
        Assert.Equal("caveat:eyes_only", caveatFailure.DenialReason);
    }

    [Fact]
    public void LevelIsCheckedBeforeTheCaveat_SoOneDenialNamesOneReason()
    {
        // Failing both must report the coarser fact. Stable reasons are what make §7's
        // audit rows and the §6.6 inspector agree.
        var marking = ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["UK"]);

        var result = ClearanceGate.Check(marking, Cleared(ClassificationLevel.Official, "NZ"));

        Assert.Equal("classification:top_secret", result.DenialReason);
    }

    [Fact]
    public void CheckClassificationAndCheckCaveat_AreTheTwoHalvesOfCheck()
    {
        // The split exists so MarkingGate can interleave the selector gates; the whole
        // must remain exactly the two halves in order, or the composed gate and this one
        // would disagree for a selector-free marking.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        foreach (var principal in new[]
        {
            Cleared(ClassificationLevel.Official, "NZ"),
            Cleared(ClassificationLevel.Secret, "NZ"),
            Cleared(ClassificationLevel.Secret, "UK"),
            PrincipalWith(),
        })
        {
            var classification = ClearanceGate.CheckClassification(marking, principal);
            var caveat = ClearanceGate.CheckCaveat(marking, principal);
            var whole = ClearanceGate.Check(marking, principal);

            var expected = classification.IsAllowed ? caveat : classification;
            Assert.Equal(expected.IsAllowed, whole.IsAllowed);
            Assert.Equal(expected.DenialReason, whole.DenialReason);
        }
    }
}
