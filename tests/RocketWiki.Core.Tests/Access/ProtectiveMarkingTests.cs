using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21: the marking value object — canonical form, the single display string
/// (§21.1's grammar: prefix, level, selectors, caveat), and the downgrade predicate that
/// decides which audit action a marking change earns.
/// </summary>
public class ProtectiveMarkingTests
{
    private static readonly SelectorCatalog Catalog = TestCatalogs.Fruit;

    private static readonly SelectorValue Apple = TestCatalogs.Apple;
    private static readonly SelectorValue Banana = TestCatalogs.Banana;
    private static readonly SelectorValue North = TestCatalogs.North;

    [Fact]
    public void Create_CanonicalizesTheCountrySet_UpperCasedTrimmedDeduplicatedAndOrdinallySorted()
    {
        var marking = ProtectiveMarking.Create(
            ClassificationLevel.Secret, ["us", " uk ", "US", "", "   ", "aus"]);

        Assert.Equal(["AUS", "UK", "US"], marking.EyesOnly);
    }

    [Fact]
    public void Create_CanonicalOrderIsStable_RegardlessOfInputOrder()
    {
        // The canonical order is what the display string, the audit DetailsJson, and the
        // sync payload all serialize, so two markings that mean the same thing must
        // render the same bytes.
        var a = ProtectiveMarking.Create(ClassificationLevel.Secret, ["US", "UK", "AUS"]);
        var b = ProtectiveMarking.Create(ClassificationLevel.Secret, ["aus", "us", "uk"]);

        Assert.Equal(a.EyesOnly, b.EyesOnly);
        Assert.Equal(a.Format(Catalog), b.Format(Catalog));
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
    public void Create_KeepsAnUnknownCaveatToken_ItMatchesNobody()
    {
        // §21.4: the value object does not filter against the fixed set. A legacy GB row
        // must read back as the thing that is enforced — and what is enforced is "no
        // principal holds it" (ResolveNationalities drops it on the other side).
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["GB"]);

        Assert.Equal(["GB"], marking.EyesOnly);
        Assert.False(ClearanceGate.Check(
            marking, Principal.Create("u", [], [new("clearance", new[] { "SECRET" }), new("nationality", new[] { "GB" })])).IsAllowed);
    }

    [Fact]
    public void Baseline_IsOfficialWithNoCaveatAndNoSelectors()
    {
        Assert.Equal(ClassificationLevel.Official, ProtectiveMarking.Baseline.Level);
        Assert.Empty(ProtectiveMarking.Baseline.EyesOnly);
        Assert.Empty(ProtectiveMarking.Baseline.Selectors);
    }

    [Fact]
    public void FailClosed_IsTopSecret_TheMostRestrictiveLevelInTheScheme()
    {
        Assert.Equal(ClassificationLevel.TopSecret, ProtectiveMarking.FailClosed.Level);
    }

    [Fact]
    public void FailClosed_CarriesNoSelectors()
    {
        // No sentinel selector is invented, for the reason no sentinel country is: TOP
        // SECRET already denies all but the highest-cleared, and a token nobody configured
        // must never enter enforcement. It can never be LESS restrictive than a real
        // marking on selectors either, because a real marking's selectors only subtract.
        Assert.Empty(ProtectiveMarking.FailClosed.Selectors);
        Assert.False(ProtectiveMarking.FailClosed.HasSelectors);
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

    // --- Selectors (design.md §21.15) -------------------------------------------------------

    [Fact]
    public void Create_CanonicalizesSelectors_AndSortsByCategory()
    {
        var marking = ProtectiveMarking.Create(
            ClassificationLevel.Secret,
            null,
            [new SelectorValue(" region ", "north"), new SelectorValue("fruit", " Apple "), new SelectorValue("FRUIT", "APPLE")]);

        // Ordinal by category (FRUIT before REGION), canonical tokens, the exact duplicate
        // collapsed - regardless of the order supplied.
        Assert.Equal([Apple, North], marking.Selectors);
        Assert.True(marking.HasSelectors);
    }

    [Fact]
    public void Create_DropsBlankSelectors()
    {
        var marking = ProtectiveMarking.Create(
            ClassificationLevel.Secret, null, [new SelectorValue("", "APPLE"), new SelectorValue("FRUIT", "  "), Apple]);

        Assert.Equal([Apple], marking.Selectors);
    }

    [Fact]
    public void Create_TwoValuesInOneCategory_Throws()
    {
        // The value object must not be constructible in an invalid state: a page carries
        // at most one value per category (also a PK fact in PageMarkingSelectors). The
        // services validate first and answer ValidationError; the importer treats it as
        // unparseable, which fails closed.
        var exception = Assert.Throws<ArgumentException>(() =>
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [Apple, Banana]));

        Assert.Contains("FRUIT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selectors_ParticipateInEquality()
    {
        var with = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [Apple]);
        var without = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]);
        var same = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [new SelectorValue("fruit", "apple")]);

        Assert.NotEqual(with, without);
        Assert.Equal(with, same);
        Assert.Equal(with.GetHashCode(), same.GetHashCode());
    }

    // --- The label (design.md §21.1) ---------------------------------------------------------

    [Theory]
    [InlineData(ClassificationLevel.Official, "OFFICIAL")]
    [InlineData(ClassificationLevel.OfficialSensitive, "OFFICIAL-SENSITIVE")]
    [InlineData(ClassificationLevel.Secret, "SECRET")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET")]
    public void Format_NoCaveat_IsTheUkWrittenFormOfTheLevel(ClassificationLevel level, string expected) =>
        Assert.Equal(expected, ProtectiveMarking.Create(level, null, prefix: null).Format(Catalog));

    [Fact]
    public void Format_OneCountry_RendersLevelThenCaveatWithoutBrackets() =>
        Assert.Equal(
            "SECRET UK EYES ONLY",
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: null).Format(Catalog));

    [Fact]
    public void Format_SeveralCountries_JoinsWithSlashes() =>
        Assert.Equal(
            "SECRET UK/US EYES ONLY",
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["US", "uk"], prefix: null).Format(Catalog));

    [Fact]
    public void Format_RendersTheFixedCaveatTokens()
    {
        // The five tokens, in their fixed ordinal order — AUS/NZ, never NZ/AUS.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["nz", "aus", "can", "us", "uk"]);

        Assert.Equal("UK SECRET AUS/CAN/NZ/UK/US EYES ONLY", marking.Format(Catalog));
    }

    [Fact]
    public void Format_PlacesSelectorsBetweenTheLevelAndTheCaveat_ValuesOnly()
    {
        // The headline grammar: <PREFIX> <CLASSIFICATION> <SELECTORS> <CAVEAT>. Values
        // only - the category name is never part of the written marking.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, ["nz", "aus"], [North, Apple]);

        Assert.Equal("UK SECRET APPLE NORTH AUS/NZ EYES ONLY", marking.Format(Catalog));
        Assert.DoesNotContain("FRUIT", marking.Format(Catalog), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OrdersSelectorsByCatalogOrder_UnknownCategoriesLast()
    {
        // Stored ordinally (COLOUR < FRUIT < REGION), but rendered in the CONFIGURED order
        // (FRUIT, REGION), with the unknown category last - and still rendered at all: an
        // unknown selector is enforced (against everyone), so the label must show it.
        var reversedCatalog = SelectorCatalog.Create(
        [
            new SelectorCategory("REGION", null, null, ["NORTH", "SOUTH"]),
            new SelectorCategory("FRUIT", null, "fruit", ["APPLE", "BANANA"]),
        ]);
        var marking = ProtectiveMarking.Create(
            ClassificationLevel.Official, null, [Apple, North, new SelectorValue("COLOUR", "RED")]);

        Assert.Equal("UK OFFICIAL APPLE NORTH RED", marking.Format(Catalog));
        Assert.Equal("UK OFFICIAL NORTH APPLE RED", marking.Format(reversedCatalog));
        Assert.Equal("UK OFFICIAL RED APPLE NORTH", marking.Format(SelectorCatalog.Empty)); // all unknown: ordinal by category
    }

    [Fact]
    public void FormatLabel_ListsSeveralCaveatSetsWithACommaNeverMerging()
    {
        // The aggregate's case (§21.13): two sources with different caveats render as a
        // conjunction, listed. Merging (AUS/NZ/US) would say any one suffices;
        // intersecting ({AUS,NZ} ∩ {US} = ∅) would render as no caveat at all.
        var label = ProtectiveMarking.FormatLabel(
            "UK", ClassificationLevel.Secret, [], [["AUS", "NZ"], ["US"]], Catalog);

        Assert.Equal("UK SECRET AUS/NZ EYES ONLY, US EYES ONLY", label);
    }

    [Fact]
    public void FormatLabel_AnAggregateMayCarryTwoValuesOfOneCategory_AndRendersBothInValueOrder()
    {
        // A page cannot (Create throws); an aggregate over an APPLE page and a BANANA
        // page can, and the union is the honest reading for selectors.
        var label = ProtectiveMarking.FormatLabel(
            "UK", ClassificationLevel.Secret, [Banana, North, Apple], [], Catalog);

        Assert.Equal("UK SECRET APPLE BANANA NORTH", label);
    }

    [Fact]
    public void FormatLabel_AnEmptyCaveatSet_ContributesNothing()
    {
        Assert.Equal(
            "SECRET",
            ProtectiveMarking.FormatLabel(null, ClassificationLevel.Secret, [], [[]], Catalog));
        Assert.Equal(
            "SECRET US EYES ONLY",
            ProtectiveMarking.FormatLabel(null, ClassificationLevel.Secret, [], [[], ["US"], []], Catalog));
    }

    // --- The national prefix in the label (design.md §21.12) ----------------------------

    [Theory]
    // All four combinations of prefix x caveat, which is the whole contract of the label.
    [InlineData("UK", new[] { "UK", "US" }, "UK SECRET UK/US EYES ONLY")]
    [InlineData("UK", new string[0], "UK SECRET")]
    [InlineData(null, new[] { "UK", "US" }, "SECRET UK/US EYES ONLY")]
    [InlineData(null, new string[0], "SECRET")]
    public void Format_PlacesThePrefixBeforeTheLevel_AndOmitsItCleanlyWhenAbsent(
        string? prefix, string[] eyesOnly, string expected) =>
        Assert.Equal(expected, ProtectiveMarking.Create(ClassificationLevel.Secret, eyesOnly, selectors: null, prefix).Format(Catalog));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_AnEmptyPrefix_LeavesNoLeadingSpace(string prefix)
    {
        // A cosmetic leading space would make two identical markings compare unequal as
        // strings, which matters because the label is what the SPA, MCP and a reviewer
        // all read.
        var marking = ProtectiveMarking.Create(ClassificationLevel.Secret, null, selectors: null, prefix);

        Assert.Equal("SECRET", marking.Format(Catalog));
        Assert.Null(marking.Prefix);
    }

    [Fact]
    public void Format_TheDefaultPrefixIsUk() =>
        // Create's default parameter, so anything that does not mention a prefix gets one.
        Assert.Equal("UK OFFICIAL", ProtectiveMarking.Create(ClassificationLevel.Official, null).Format(Catalog));

    [Fact]
    public void UkPrefix_IsTheDefaultPrefix_TheToggleHasExactlyOneOnValue() =>
        Assert.Equal(ProtectiveMarking.DefaultPrefix, ProtectiveMarking.UkPrefix);

    [Fact]
    public void Baseline_IsUkOfficial() => Assert.Equal("UK OFFICIAL", ProtectiveMarking.Baseline.Format(Catalog));

    [Fact]
    public void FailClosed_CarriesNoPrefix_BecauseAMissingMarkingSaidNothingAboutOne() =>
        // Asserting UK on a marking we know nothing about would be inventing a fact; the
        // bare "TOP SECRET" is also a quiet signal that this page's row is missing.
        Assert.Equal("TOP SECRET", ProtectiveMarking.FailClosed.Format(Catalog));

    [Theory]
    [InlineData("uk", "UK")]
    [InlineData("  Uk  ", "UK")]
    [InlineData("nato", "NATO")]
    public void Create_NormalizesThePrefix_UpperCasedAndTrimmed(string input, string expected) =>
        // The value object still accepts any string, so a legacy row or an older bundle
        // renders verbatim; the mutation is where the toggle lives (§21.12).
        Assert.Equal(expected, ProtectiveMarking.Create(ClassificationLevel.Secret, null, prefix: input).Prefix);

    // --- PageMarkingView ------------------------------------------------------------------

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
        var view = PageMarkingView.From(ProtectiveMarking.Create(level, ["UK"], [Apple], "UK"), Catalog);

        Assert.Equal(expectedLevelName, view.LevelName);
        Assert.Equal(ProtectiveMarking.LevelName(level), view.LevelName);

        // And the composed label is still the WHOLE marking - the two are not
        // interchangeable, which is the mistake the names are shaped to prevent.
        Assert.Equal($"UK {expectedLevelName} APPLE UK EYES ONLY", view.Label);
        Assert.NotEqual(view.Label, view.LevelName);
        Assert.Equal([Apple], view.Selectors);
        Assert.Equal(["UK"], view.EyesOnly);
    }

    [Fact]
    public void PageMarkingView_LevelName_IgnoresPrefixSelectorsAndCaveat_ItIsTheLevelAlone()
    {
        var bare = PageMarkingView.From(ProtectiveMarking.Create(ClassificationLevel.Secret, null, prefix: null), Catalog);
        var dressed = PageMarkingView.From(ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"], [Apple], "NATO"), Catalog);

        Assert.Equal("SECRET", bare.LevelName);
        Assert.Equal("SECRET", dressed.LevelName);
    }

    [Theory]
    [InlineData("UK", true, "UK SECRET")]
    [InlineData(null, false, "SECRET")]
    [InlineData("NATO", false, "NATO SECRET")]  // a legacy prefix: not the toggle's on-state, but still rendered verbatim
    public void PageMarkingView_UkPrefix_IsTheToggle_AndTheLabelStillShowsWhatIsStored(
        string? prefix, bool expectedToggle, string expectedLabel)
    {
        var view = PageMarkingView.From(ProtectiveMarking.Create(ClassificationLevel.Secret, null, selectors: null, prefix), Catalog);

        Assert.Equal(expectedToggle, view.UkPrefix);
        Assert.Equal(expectedLabel, view.Label);
    }

    [Fact]
    public void Prefix_ParticipatesInEquality_ButNotInTheDowngradePredicate()
    {
        var withPrefix = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: "UK");
        var without = ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: null);

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
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [Apple]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], [Apple])));

    [Fact]
    public void IsDowngrade_ClearingTheCaveat_IsADowngrade() =>
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, [])));

    [Fact]
    public void IsDowngrade_AddingACountryToTheCaveat_IsADowngrade_ItAdmitsMorePeople() =>
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"])));

    [Fact]
    public void IsDowngrade_NarrowingTheCaveat_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK", "US"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"])));

    [Fact]
    public void IsDowngrade_AddingACaveatWhereThereWasNone_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, []),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"])));

    [Fact]
    public void IsDowngrade_SwappingOneCountryForAnother_IsADowngrade_SomebodyNewCanReadIt() =>
        // Even though UK loses access, US gains it - and "somebody who could not read
        // this yesterday can read it today" is exactly what a reviewer is looking for.
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, ["US"])));

    [Fact]
    public void IsDowngrade_RaisingTheLevelWhileRelaxingTheCaveat_IsStillADowngrade() =>
        // The level went up, but the caveat was cleared: a SECRET-cleared NZ national
        // could not read it before and can now. Erring toward "call it a downgrade" is
        // the safe error - the cost is one extra row in a reviewer's result set.
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.OfficialSensitive, ["UK"]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, [])));

    [Fact]
    public void IsDowngrade_RemovingASelector_IsADowngrade() =>
        // A selector only subtracts readers, so taking it away admits everyone it excluded.
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [Apple, North]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [North])));

    [Fact]
    public void IsDowngrade_SwappingAValueWithinACategory_IsADowngrade() =>
        // APPLE -> BANANA removes APPLE: every BANANA holder who lacked APPLE can now read it.
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [Apple]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [Banana])));

    [Fact]
    public void IsDowngrade_AddingASelector_IsNot() =>
        Assert.False(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [Apple]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, null, [Apple, North])));

    [Fact]
    public void IsDowngrade_RaisingTheLevelWhileRemovingASelector_IsStillADowngrade() =>
        Assert.True(ProtectiveMarking.IsDowngrade(
            ProtectiveMarking.Create(ClassificationLevel.Official, null, [Apple]),
            ProtectiveMarking.Create(ClassificationLevel.Secret, null)));
}
