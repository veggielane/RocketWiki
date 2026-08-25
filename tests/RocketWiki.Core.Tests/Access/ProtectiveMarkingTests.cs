using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21: the marking value object — canonical form, the single display string,
/// and the downgrade predicate that decides which audit action a marking change earns.
/// </summary>
public class ProtectiveMarkingTests
{
    [Fact]
    public void Create_CanonicalizesTheCountrySet_UpperCasedTrimmedDeduplicatedAndOrdinallySorted()
    {
        var marking = ProtectiveMarking.Create(
            ClassificationLevel.Secret, ["us", " gb ", "US", "", "   ", "au"]);

        Assert.Equal(["AU", "GB", "US"], marking.EyesOnly);
    }

    [Fact]
    public void Create_CanonicalOrderIsStable_RegardlessOfInputOrder()
    {
        // The canonical order is what the display string, the audit DetailsJson, and the
        // sync payload all serialize, so two markings that mean the same thing must
        // render the same bytes.
        var a = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US", "GB", "AU"]);
        var b = ProtectiveMarking.Create(ClassificationLevel.Secret, ["au", "us", "gb"]);

        Assert.Equal(a.EyesOnly, b.EyesOnly);
        Assert.Equal(a.Format(), b.Format());
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Create_NullCountrySet_IsNoCaveat()
    {
        var marking = ProtectiveMarking.Create(ClassificationLevel.Official, null);

        Assert.Empty(marking.EyesOnly);
        Assert.False(marking.HasEyesOnly);
    }

    [Fact]
    public void Baseline_IsOfficialWithNoCaveat()
    {
        Assert.Equal(ClassificationLevel.Official, ProtectiveMarking.Baseline.Level);
        Assert.Empty(ProtectiveMarking.Baseline.EyesOnly);
    }

    [Fact]
    public void FailClosed_IsTopSecret_TheMostRestrictiveLevelInTheScheme()
    {
        Assert.Equal(ClassificationLevel.TopSecret, ProtectiveMarking.FailClosed.Level);
    }

    [Fact]
    public void DefaultClassificationLevel_IsNotAValidLevel_SoAnUninitializedStructCannotReadAsOfficial()
    {
        Assert.False(Enum.IsDefined(default(ClassificationLevel)));
        Assert.Equal(1, (byte)ClassificationLevel.Official);
    }

    [Theory]
    [InlineData(0)]     // an uninitialized tinyint — the dangerous one: BELOW the ladder
    [InlineData(7)]     // above the ladder
    [InlineData(255)]
    public void Create_ALevelOutsideTheLadder_BecomesTopSecret_NeverASilentBypass(byte raw)
    {
        // The column is a tinyint, so a hand-edited row or a botched restore can present
        // a value the enum does not define. A value BELOW the ladder would compare as
        // less than every clearance and make the page readable by everybody - a silent
        // bypass of the entire control. Normalized at the one constructor instead.
        var marking = ProtectiveMarking.Create((ClassificationLevel)raw, null);

        Assert.Equal(ClassificationLevel.TopSecret, marking.Level);
        Assert.False(ClearanceGate.Check(
            marking,
            Principal.Create("u", [], [new("clearance", new[] { "SECRET" })])).IsAllowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void LevelNaming_NeverThrows_BecauseAThrowOnAReadPathWouldBeADistinguishable500(byte raw)
    {
        // A 500 is distinguishable from a not-found, which is precisely the §6.7 leak the
        // denial design exists to prevent - so these answer with the most restrictive
        // value rather than throwing on a corrupt input.
        var level = (ClassificationLevel)raw;

        Assert.Equal("TOP SECRET", ProtectiveMarking.LevelName(level));
        Assert.Equal("TOP_SECRET", ProtectiveMarking.LevelWireName(level));
        Assert.Equal("top_secret", ProtectiveMarking.LevelToken(level));
    }

    [Theory]
    [InlineData(ClassificationLevel.Official, "OFFICIAL")]
    [InlineData(ClassificationLevel.OfficialSensitive, "OFFICIAL-SENSITIVE")]
    [InlineData(ClassificationLevel.Secret, "SECRET")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET")]
    public void Format_NoCaveat_IsTheUkWrittenFormOfTheLevel(ClassificationLevel level, string expected) =>
        Assert.Equal(expected, ProtectiveMarking.Create(level, null, prefix: null).Format());

    [Fact]
    public void Format_OneCountry_RendersTheEyesOnlyCaveatAfterTheLevel() =>
        Assert.Equal(
            "SECRET [UK EYES ONLY]",
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: null).Format());

    [Fact]
    public void Format_SeveralCountries_JoinsThemWithSlashesInCanonicalOrder() =>
        Assert.Equal(
            "SECRET [UK/US EYES ONLY]",
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["US", "uk"], prefix: null).Format());

    [Fact]
    public void Format_UsesTheInstancesOwnCountryTokensVerbatim_NeverAliasingGbToUk() =>
        // A marking must read back as the thing that is actually enforced. A display-only
        // GB->UK alias is how "we thought it said UK" happens.
        Assert.Equal(
            "SECRET [GB EYES ONLY]",
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"], prefix: null).Format());

    // --- The national prefix in the label (design.md §21.12) ----------------------------

    [Theory]
    // All four combinations of prefix x caveat, which is the whole contract of the label.
    [InlineData("UK", new[] { "UK", "US" }, "UK SECRET [UK/US EYES ONLY]")]
    [InlineData("UK", new string[0], "UK SECRET")]
    [InlineData(null, new[] { "UK", "US" }, "SECRET [UK/US EYES ONLY]")]
    [InlineData(null, new string[0], "SECRET")]
    public void Format_PlacesThePrefixBeforeTheLevel_AndOmitsItCleanlyWhenAbsent(
        string? prefix, string[] eyesOnly, string expected) =>
        Assert.Equal(expected, ProtectiveMarking.Create(ClassificationLevel.Secret, eyesOnly, prefix).Format());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_AnEmptyPrefix_LeavesNoLeadingSpace(string prefix)
    {
        // A cosmetic leading space would make two identical markings compare unequal as
        // strings, which matters because the label is what the SPA, MCP and a reviewer
        // all read.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, null, prefix);

        Assert.Equal("SECRET", marking.Format());
        Assert.Null(marking.Prefix);
    }

    [Fact]
    public void Format_TheDefaultPrefixIsUk() =>
        // Create's default parameter, so anything that does not mention a prefix gets one.
        Assert.Equal("UK OFFICIAL", ProtectiveMarking.Create(ClassificationLevel.Official, null).Format());

    [Fact]
    public void Baseline_IsUkOfficial() => Assert.Equal("UK OFFICIAL", ProtectiveMarking.Baseline.Format());

    [Fact]
    public void FailClosed_CarriesNoPrefix_BecauseAMissingMarkingSaidNothingAboutOne() =>
        // Asserting UK on a marking we know nothing about would be inventing a fact; the
        // bare "TOP SECRET" is also a quiet signal that this page's row is missing.
        Assert.Equal("TOP SECRET", ProtectiveMarking.FailClosed.Format());

    [Theory]
    [InlineData("uk", "UK")]
    [InlineData("  Uk  ", "UK")]
    [InlineData("nato", "NATO")]
    public void Create_NormalizesThePrefix_UpperCasedAndTrimmed(string input, string expected) =>
        Assert.Equal(expected, ProtectiveMarking.Create(ClassificationLevel.Secret, null, input).Prefix);

    [Theory]
    [InlineData(ClassificationLevel.Official, "OFFICIAL")]
    [InlineData(ClassificationLevel.OfficialSensitive, "OFFICIAL-SENSITIVE")]
    [InlineData(ClassificationLevel.Secret, "SECRET")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET")]
    public void PageMarkingView_CarriesTheLevelsOwnDisplaySpellingBesideTheComposedLabel(
        ClassificationLevel level, string expectedLevelName)
    {
        // design.md §21.1: a level has three spellings and one method each, ON THE SERVER,
        // so they cannot drift. Surfaces with nowhere to put a full marking - a one-word
        // list badge, a radio option - need the display spelling of a level alone, and
        // without this they would transliterate the GraphQL enum, which is a machine
        // identifier and not a marking.
        var view = PageMarkingView.From(ProtectiveMarking.Create(level, ["GB"], "UK"));

        Assert.Equal(expectedLevelName, view.LevelName);
        Assert.Equal(ProtectiveMarking.LevelName(level), view.LevelName);

        // And the composed label is still the WHOLE marking - the two are not
        // interchangeable, which is the mistake the names are shaped to prevent.
        Assert.Equal($"UK {expectedLevelName} [GB EYES ONLY]", view.Label);
        Assert.NotEqual(view.Label, view.LevelName);
    }

    [Fact]
    public void PageMarkingView_LevelName_IgnoresPrefixAndCaveat_ItIsTheLevelAlone()
    {
        var bare = PageMarkingView.From(ProtectiveMarking.Create(ClassificationLevel.Secret, null, prefix: null));
        var dressed = PageMarkingView.From(ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB", "US"], "NATO"));

        Assert.Equal("SECRET", bare.LevelName);
        Assert.Equal("SECRET", dressed.LevelName);
    }

    [Fact]
    public void Prefix_ParticipatesInEquality_ButNotInTheDowngradePredicate()
    {
        var withPrefix = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"], "UK");
        var without = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"], prefix: null);

        // Different markings — they render differently.
        Assert.NotEqual(withPrefix, without);

        // But neither direction is a downgrade: a downgrade means somebody who could not
        // read the page yesterday can read it today, and the prefix cannot move that line.
        Assert.False(ProtectiveMarking.IsDowngrade(withPrefix, without));
        Assert.False(ProtectiveMarking.IsDowngrade(without, withPrefix));
    }

    [Theory]
    [InlineData(ClassificationLevel.Official, "OFFICIAL")]
    [InlineData(ClassificationLevel.OfficialSensitive, "OFFICIAL_SENSITIVE")]
    [InlineData(ClassificationLevel.Secret, "SECRET")]
    [InlineData(ClassificationLevel.TopSecret, "TOP_SECRET")]
    public void LevelWireName_RoundTripsThroughTheClaimParser(ClassificationLevel level, string expected)
    {
        // The claim vocabulary, the sync wire format, the audit DetailsJson and the
        // GraphQL enum are all this one spelling. If they ever diverge, a token minted
        // for one stops working with another.
        Assert.Equal(expected, ProtectiveMarking.LevelWireName(level));
        Assert.True(ClearanceGate.TryParseLevel(expected, out var parsed));
        Assert.Equal(level, parsed);
    }

    // --- Downgrade predicate ------------------------------------------------------------

    [Fact]
    public void IsDowngrade_LoweringTheLevel_IsADowngrade() =>
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, null),
            ProtectiveMarking.Create(ClassificationLevel.Official, null)));

    [Fact]
    public void IsDowngrade_RaisingTheLevel_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Official, null),
            ProtectiveMarking.Create(ClassificationLevel.Secret, null)));

    [Fact]
    public void IsDowngrade_NoChangeAtAll_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"])));

    [Fact]
    public void IsDowngrade_ClearingTheCaveat_IsADowngrade() =>
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, [])));

    [Fact]
    public void IsDowngrade_AddingACountryToTheCaveat_IsADowngrade_ItAdmitsMorePeople() =>
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB", "US"])));

    [Fact]
    public void IsDowngrade_NarrowingTheCaveat_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB", "US"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"])));

    [Fact]
    public void IsDowngrade_AddingACaveatWhereThereWasNone_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, []),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"])));

    [Fact]
    public void IsDowngrade_SwappingOneCountryForAnother_IsADowngrade_SomebodyNewCanReadIt() =>
        // Even though GB loses access, US gains it - and "somebody who could not read
        // this yesterday can read it today" is exactly what a reviewer is looking for.
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"])));

    [Fact]
    public void IsDowngrade_RaisingTheLevelWhileRelaxingTheCaveat_IsStillADowngrade() =>
        // The level went up, but the caveat was cleared: a SECRET-cleared NZ national
        // could not read it before and can now. Erring toward "call it a downgrade" is
        // the safe error - the cost is one extra row in a reviewer's result set.
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.OfficialSensitive, ["GB"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, [])));
}
