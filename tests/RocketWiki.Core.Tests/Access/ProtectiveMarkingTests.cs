using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
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
    [InlineData(ClassificationLevel.Official, "OFFICIAL")]
    [InlineData(ClassificationLevel.OfficialSensitive, "OFFICIAL-SENSITIVE")]
    [InlineData(ClassificationLevel.Secret, "SECRET")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET")]
    public void Format_NoCaveat_IsTheUkWrittenFormOfTheLevel(ClassificationLevel level, string expected) =>
        Assert.Equal(expected, ProtectiveMarking.Create(level, null).Format());

    [Fact]
    public void Format_OneCountry_RendersTheEyesOnlyCaveatAfterTheLevel() =>
        Assert.Equal("SECRET [UK EYES ONLY]", ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]).Format());

    [Fact]
    public void Format_SeveralCountries_JoinsThemWithSlashesInCanonicalOrder() =>
        Assert.Equal(
            "SECRET [UK/US EYES ONLY]",
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["US", "uk"]).Format());

    [Fact]
    public void Format_UsesTheInstancesOwnCountryTokensVerbatim_NeverAliasingGbToUk() =>
        // A marking must read back as the thing that is actually enforced. A display-only
        // GB->UK alias is how "we thought it said UK" happens.
        Assert.Equal("SECRET [GB EYES ONLY]", ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]).Format());

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
