using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.4: the caveat decision itself, in isolation from the permission
/// computation that AND-s it onto canView. These are the tests that pin the fail-closed
/// choices — what a nationality outside the fixed set is worth, and what an eyes-only
/// caveat does with a principal who has no nationality.
///
/// <para>This file was <c>ClearanceGateTests</c>, and half of it — the 4×4 clearance
/// ladder, the OFFICIAL-SENSITIVE floor, the multi-valued claim rules — is gone with the
/// level gate: this deployment carries no clearance attribute, so there is no ladder to
/// drive. The level is presentational now (§21.12), and the prefix-invariance sweep
/// below still ranges over every level to prove neither one reaches a verdict.</para>
/// </summary>
public class CaveatGateTests
{
    private static Principal PrincipalWith(params (string Key, string[] Values)[] attributes) =>
        Principal.Create(
            "user-sub",
            [],
            attributes.Select(a => new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values)));

    private static Principal National(params string[] nationalities) =>
        nationalities.Length == 0 ? PrincipalWith() : PrincipalWith(("nationality", nationalities));

    // --- Nationality resolution (design.md §21.4) ------------------------------------------

    [Fact]
    public void ResolveNationalities_IgnoresValuesOutsideTheFixedSet_SoAMapperEmittingGbHoldsNothing()
    {
        // The reversed §21.4: one hard-coded vocabulary on both sides, so the only failure
        // left is a mapper emitting a foreign token - and that becomes an EMPTY nationality
        // (visible in me.nationality), never a silent partial match.
        var held = CaveatGate.ResolveNationalities(PrincipalWith(("nationality", ["GB", "gbr", " nz ", "FR"])));

        Assert.Equal(["NZ"], held);
        Assert.Empty(CaveatGate.ResolveNationalities(PrincipalWith(("nationality", ["GB"]))));
        Assert.Empty(CaveatGate.ResolveNationalities(PrincipalWith()));
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("gb")]
    [InlineData("GBR")]
    [InlineData("UNITED KINGDOM")]
    [InlineData("FR")]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveNationalities_AnUnrecognisedToken_HoldsNothing(string token)
    {
        // Not "holds the token as written" and not "holds the nearest known country":
        // holds NOTHING, so every caveated page is closed to this principal until the
        // realm's mapper is fixed. The me query echoes the empty set, which is how the
        // misconfiguration is diagnosed rather than half-worked-around.
        var principal = PrincipalWith(("nationality", [token]));

        Assert.Empty(CaveatGate.ResolveNationalities(principal));
        Assert.False(CaveatGate.Check(ProtectiveMarking.Create(ClassificationLevel.Official, ["UK"]), principal).IsAllowed);
        Assert.False(CaveatGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.Official, NationalCaveatVocabulary.Values), principal).IsAllowed);
    }

    // --- Eyes-only caveat ---------------------------------------------------------------

    [Fact]
    public void EyesOnly_SingleCountry_AdmitsAMatchingNationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);

        Assert.True(CaveatGate.Check(marking, National("UK")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_MultipleCountries_AdmitAnyOneOfThem()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"]);

        Assert.True(CaveatGate.Check(marking, National("US")).IsAllowed);
        Assert.True(CaveatGate.Check(marking, National("UK")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_DualNational_MatchesOnEitherNationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"]);

        Assert.True(CaveatGate.Check(marking, National("UK", "US")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_NoOverlap_IsDenied_WithTheCaveatReason_NamingNoCountry()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"]);

        var result = CaveatGate.Check(marking, National("NZ"));

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
        var noNationality = PrincipalWith(("groups-are-not-nationality", ["UK"]));

        var result = CaveatGate.Check(marking, noNationality);

        Assert.False(result.IsAllowed);
        Assert.Equal("caveat:eyes_only", result.DenialReason);
    }

    [Fact]
    public void EyesOnly_EmptyNationalityValues_IsDenied()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        var blank = PrincipalWith(("nationality", ["", "  "]));

        Assert.False(CaveatGate.Check(marking, blank).IsAllowed);
    }

    [Fact]
    public void EyesOnly_ALegacyGbToken_OnEitherSide_MatchesNobody()
    {
        // A GB row (pre-migration data, or an older bundle) is kept verbatim on the
        // marking and dropped on the principal side, so it can never match - fail closed
        // rather than a display-only alias to UK that enforces something else.
        var legacyRow = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]);

        Assert.False(CaveatGate.Check(legacyRow, National("GB")).IsAllowed);
        Assert.False(CaveatGate.Check(legacyRow, National("UK")).IsAllowed);
        Assert.False(CaveatGate.Check(ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]), National("GB")).IsAllowed);
    }

    [Fact]
    public void EyesOnly_EmptySet_IsNoCaveatAtAll_EvenWithoutANationality()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, []);

        Assert.True(CaveatGate.Check(marking, National()).IsAllowed);
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

        Assert.True(CaveatGate.Check(marking, National(heldNationality)).IsAllowed);
    }

    // --- The level and the prefix are outside the gate (design.md §21.12) -----------------

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

                foreach (var principal in new[] { PrincipalWith(), National("UK"), National("NZ"), National("UK", "US") })
                {
                    var without = CaveatGate.Check(baseline, principal);
                    var with = CaveatGate.Check(prefixed, principal);

                    Assert.Equal(without.IsAllowed, with.IsAllowed);
                    // Reasons too: a prefix must not leak into the audit vocabulary any
                    // more than into the verdict.
                    Assert.Equal(without.DenialReason, with.DenialReason);
                }
            }
        }
    }

    [Fact]
    public void Check_IgnoresTheLevelEntirely_EveryLevelGivesTheSameVerdict()
    {
        // The level's twin of the prefix sweep, and the test that would go red if someone
        // put a clearance comparison back "because TOP SECRET should mean something": over
        // every level, the same caveat and the same principal must produce the same
        // verdict and the same reason, because the only thing this gate reads is the
        // eyes-only set (§21.12, now covering the level).
        foreach (string[] countries in new[] { Array.Empty<string>(), ["UK"], ["NZ"] })
        foreach (var principal in new[] { PrincipalWith(), National("UK"), National("NZ") })
        {
            var verdicts = Enum.GetValues<ClassificationLevel>()
                .Select(level => CaveatGate.Check(ProtectiveMarking.Create(level, countries), principal))
                .ToList();

            Assert.Single(verdicts.Select(v => v.IsAllowed).Distinct());
            Assert.Single(verdicts.Select(v => v.DenialReason).Distinct());
        }

        // Non-vacuous: the sweep above must contain both an allow and a deny.
        Assert.True(CaveatGate.Check(ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["UK"]), National("UK")).IsAllowed);
        Assert.False(CaveatGate.Check(ProtectiveMarking.Create(ClassificationLevel.Official, ["UK"]), National("NZ")).IsAllowed);
    }

    [Fact]
    public void ADenialReason_NeverMentionsThePrefixOrTheLevel()
    {
        var caveatFailure = CaveatGate.Check(
            ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["UK"], prefix: "ZZPREFIXSENTINELZZ"),
            National("NZ"));

        Assert.DoesNotContain("ZZPREFIXSENTINELZZ", caveatFailure.DenialReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", caveatFailure.DenialReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("caveat:eyes_only", caveatFailure.DenialReason);
    }
}
