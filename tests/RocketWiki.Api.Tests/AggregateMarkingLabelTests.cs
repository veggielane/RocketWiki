using System.Reflection;
using RocketWiki.Api.Markings;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §21.13: the aggregate marking label — pure logic, so a pure unit test
/// (§14's first tier). Four families of claim live here:
///
/// <list type="number">
/// <item><b>The rule.</b> Level is the maximum over contributors, swept exhaustively.</item>
/// <item><b>The conjunction, and the trap.</b> Distinct eyes-only sets are listed, never
/// merged and never intersected — because an intersection of two disjoint sets is EMPTY,
/// and an empty caveat in this model reads as "no caveat at all": the least restrictive
/// possible answer from the two most restrictive inputs. The corresponding invariant is
/// swept over every subset of a marked corpus: the aggregate's caveat is empty <b>iff</b>
/// no source had one.</item>
/// <item><b>Selectors are the union</b> (§21.15), rendered in configured order.</item>
/// <item><b>That it cannot gate.</b> Structural, not behavioural — see the last two
/// tests.</item>
/// </list>
/// </summary>
public class AggregateMarkingLabelTests
{
    private static readonly ClassificationLevel[] AllLevels =
        [.. Enum.GetValues<ClassificationLevel>()];

    private static readonly SelectorCatalog Catalog = SelectorCatalog.Create(
    [
        new SelectorCategory("FRUIT", "Fruit compartments", "fruit", ["APPLE", "BANANA"]),
        new SelectorCategory("REGION", "Regional releasability", null, ["NORTH", "SOUTH"]),
    ]);

    private static readonly SelectorValue Apple = new("FRUIT", "APPLE");
    private static readonly SelectorValue Banana = new("FRUIT", "BANANA");
    private static readonly SelectorValue North = new("REGION", "NORTH");

    private static ProtectiveMarking Mark(
        ClassificationLevel level,
        string[]? eyesOnly = null,
        string? prefix = ProtectiveMarking.DefaultPrefix,
        SelectorValue[]? selectors = null) =>
        ProtectiveMarking.Create(level, eyesOnly, selectors, prefix);

    private static AggregateMarkingLabel? Of(params ProtectiveMarking[] sources) => AggregateMarkingLabel.Of(sources, Catalog);

    // ---------- level: the maximum, over every combination ----------

    [Fact]
    public void Level_IsTheMaximum_OverEveryPairInTheScheme()
    {
        foreach (var a in AllLevels)
        {
            foreach (var b in AllLevels)
            {
                var label = Of(Mark(a), Mark(b));

                Assert.NotNull(label);
                Assert.Equal((ClassificationLevel)Math.Max((int)a, (int)b), label.Level);
            }
        }
    }

    [Fact]
    public void Level_IsTheMaximum_OverEveryTripleInTheScheme()
    {
        foreach (var a in AllLevels)
        {
            foreach (var b in AllLevels)
            {
                foreach (var c in AllLevels)
                {
                    var label = Of(Mark(a), Mark(b), Mark(c));

                    Assert.Equal(AllLevels.Where(l => l == a || l == b || l == c).Max(), label!.Level);
                }
            }
        }
    }

    [Fact]
    public void Level_OneSecretSourceRaisesAnOtherwiseOfficialAnswer()
    {
        // The headline case in prose form: four OFFICIAL sources and one SECRET make a
        // SECRET compilation. This is the whole doctrine in one assertion.
        var label = Of(
            Mark(ClassificationLevel.Official),
            Mark(ClassificationLevel.Official),
            Mark(ClassificationLevel.Secret),
            Mark(ClassificationLevel.Official));

        Assert.Equal("UK SECRET", label!.Label);
    }

    // ---------- single source, no source ----------

    [Fact]
    public void SingleSource_RendersExactlyThatMarkingsOwnLabel_ThroughTheSameFormatter()
    {
        // §21.1's one-formatter rule, mechanized: an aggregate over one page must be
        // byte-identical to that page's own marking.label. If these ever diverge, two
        // renderings of one marking exist, which is a compliance problem.
        ProtectiveMarking[] markings =
        [
            ProtectiveMarking.Baseline,
            ProtectiveMarking.FailClosed,
            Mark(ClassificationLevel.Secret, ["UK"]),
            Mark(ClassificationLevel.OfficialSensitive, ["UK", "US"], prefix: null),
            Mark(ClassificationLevel.Secret, ["NZ"], selectors: [Apple, North]),
            Mark(ClassificationLevel.TopSecret, null, prefix: null, selectors: [Banana]),
        ];

        foreach (var marking in markings)
        {
            var label = Of(marking);

            Assert.Equal(marking.Format(Catalog), label!.Label);
            Assert.Equal(PageMarkingView.From(marking, Catalog).Label, label.Label);
            Assert.Equal(marking.Level, label.Level);
            Assert.Equal(marking.Selectors, label.Selectors);
        }
    }

    [Fact]
    public void NoSources_IsNoLabelAtAll_NotOfficialAndNotTopSecret()
    {
        // Nothing was shown, so there is nothing to mark. OFFICIAL would assert a
        // reviewed judgement about content that does not exist (§21.11's complaint about
        // the backfill); TOP SECRET would invent a fact. Null is the honest answer, and
        // the consuming GraphQL fields are nullable to carry it.
        Assert.Null(Of());
    }

    // ---------- the caveat conjunction, and the empty-intersection trap ----------

    [Fact]
    public void TwoDifferentCaveats_AreListedAsAConjunction_NeverMergedAndNeverIntersected()
    {
        var label = Of(
            Mark(ClassificationLevel.Secret, ["UK"]),
            Mark(ClassificationLevel.Secret, ["US"]));

        // The honest reading: a reader needs BOTH. Listed, in canonical order, comma-separated.
        Assert.Equal("UK SECRET UK EYES ONLY, US EYES ONLY", label!.Label);
        Assert.Equal([["UK"], ["US"]], label.EyesOnlySets);

        // The union would say either nationality suffices — a widening, and false.
        Assert.DoesNotContain("UK/US EYES ONLY", label.Label);
    }

    [Fact]
    public void DisjointCaveats_DoNotCollapseToNoCaveat_TheEmptyIntersectionTrap()
    {
        // THE trap this design exists to avoid. {UK} ∩ {US} is empty, and an empty
        // eyes-only set in this model means NO CAVEAT — so an intersecting implementation
        // would turn the two most restrictive inputs available into the least restrictive
        // possible output, silently and while looking perfectly correct. Same shape as the
        // level-0 trap ProtectiveMarking.Create normalizes away.
        var label = Of(
            Mark(ClassificationLevel.Secret, ["UK"]),
            Mark(ClassificationLevel.Secret, ["US"]));

        Assert.NotEmpty(label!.EyesOnlySets);
        Assert.Equal(2, label.EyesOnlySets.Count);
        Assert.NotEqual("UK SECRET", label.Label);
        Assert.Contains("EYES ONLY", label.Label);
    }

    [Fact]
    public void IdenticalCaveatSets_CollapseToOneEntry()
    {
        var label = Of(
            Mark(ClassificationLevel.Secret, ["UK", "US"]),
            Mark(ClassificationLevel.Official, ["US", "UK"]), // same set, different input order
            Mark(ClassificationLevel.Secret, ["uk", " us "])); // same set, canonicalized on the way in

        Assert.Equal("UK SECRET UK/US EYES ONLY", label!.Label);
        Assert.Single(label.EyesOnlySets);
    }

    [Fact]
    public void ASourceWithNoCaveat_ContributesNoCaveat()
    {
        var label = Of(
            Mark(ClassificationLevel.Official),
            Mark(ClassificationLevel.Secret, ["UK"]));

        Assert.Equal("UK SECRET UK EYES ONLY", label!.Label);
        Assert.Single(label.EyesOnlySets);
    }

    [Fact]
    public void CaveatOrder_IsAPropertyOfTheContent_NotOfRetrievalOrder()
    {
        // Two identical result sets retrieved in different rank orders must render the
        // same bytes; otherwise "the marking changed" becomes a meaningless observation.
        ProtectiveMarking[] sources =
        [
            Mark(ClassificationLevel.Secret, ["US"]),
            Mark(ClassificationLevel.Official, ["AUS"]),
            Mark(ClassificationLevel.Secret, ["UK"]),
        ];

        var forward = AggregateMarkingLabel.Of(sources, Catalog);
        var reversed = AggregateMarkingLabel.Of(sources.Reverse(), Catalog);

        Assert.Equal("UK SECRET AUS EYES ONLY, UK EYES ONLY, US EYES ONLY", forward!.Label);
        Assert.Equal(forward.Label, reversed!.Label);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void EmptyAggregateCaveat_MeansNoSourceHadOne_AndIsUnreachableByAnyOtherPath()
    {
        // The invariant the whole conjunction design rests on, swept exhaustively over
        // every non-empty subset of a corpus that mixes caveated and uncaveated markings
        // at every level: the aggregate's caveat list is empty IFF no contributor had a
        // caveat. No subtraction, no intersection, no set algebra can reach empty.
        ProtectiveMarking[] corpus =
        [
            Mark(ClassificationLevel.Official),
            Mark(ClassificationLevel.OfficialSensitive, ["UK"]),
            Mark(ClassificationLevel.Secret, ["US"]),
            Mark(ClassificationLevel.Secret, ["UK", "US"]),
            Mark(ClassificationLevel.TopSecret, selectors: [Apple]),
            ProtectiveMarking.FailClosed,
        ];

        for (var mask = 1; mask < 1 << corpus.Length; mask++)
        {
            var subset = corpus.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var label = AggregateMarkingLabel.Of(subset, Catalog);

            var anySourceHadACaveat = subset.Any(m => m.HasEyesOnly);
            Assert.Equal(anySourceHadACaveat, label!.EyesOnlySets.Count > 0);

            // And the rendered form agrees with the structure: no caveat sets, no caveat
            // text — never a dangling "EYES ONLY".
            Assert.Equal(anySourceHadACaveat, label.Label.Contains("EYES ONLY", StringComparison.Ordinal));
        }
    }

    // ---------- selectors: the union, in configured order ----------

    [Fact]
    public void Selectors_AreTheUnionOverSources_RenderedInConfiguredOrder()
    {
        // An APPLE page and a BANANA NORTH page: material that needed all three. A page
        // cannot carry two values of one category; an aggregate can, and must say so.
        var label = Of(
            Mark(ClassificationLevel.Official, selectors: [North, Banana]),
            Mark(ClassificationLevel.Secret, selectors: [Apple]),
            Mark(ClassificationLevel.Official));

        Assert.Equal([Apple, Banana, North], label!.Selectors);
        Assert.Equal("UK SECRET APPLE BANANA NORTH", label.Label);
    }

    [Fact]
    public void Selectors_IdenticalValuesCollapse_AndOrderIsAPropertyOfTheContent()
    {
        ProtectiveMarking[] sources =
        [
            Mark(ClassificationLevel.Secret, selectors: [Apple]),
            Mark(ClassificationLevel.Secret, selectors: [North]),
            Mark(ClassificationLevel.Secret, selectors: [Apple, North]),
        ];

        var forward = AggregateMarkingLabel.Of(sources, Catalog);
        var reversed = AggregateMarkingLabel.Of(sources.Reverse(), Catalog);

        Assert.Equal([Apple, North], forward!.Selectors);
        Assert.Equal(forward, reversed);
        Assert.Equal("UK SECRET APPLE NORTH", forward.Label);
    }

    [Fact]
    public void Selectors_AndCaveatSets_ComposeInTheOneGrammar()
    {
        var label = Of(
            Mark(ClassificationLevel.Secret, ["AUS", "NZ"], selectors: [Apple]),
            Mark(ClassificationLevel.Official, ["US"], selectors: [North]));

        Assert.Equal("UK SECRET APPLE NORTH AUS/NZ EYES ONLY, US EYES ONLY", label!.Label);
    }

    // ---------- prefix ----------

    [Fact]
    public void Prefix_UnanimousUkCarriesThrough()
    {
        var label = Of(
            Mark(ClassificationLevel.Official, prefix: "UK"),
            Mark(ClassificationLevel.Secret, prefix: "uk")); // canonicalized to UK on creation

        Assert.True(label!.UkPrefix);
        Assert.Equal("UK SECRET", label.Label);
    }

    [Fact]
    public void Prefix_DisagreementDropsToNone_RatherThanPickingAWinner()
    {
        // The prefix gates nothing (§21.12), so the only way it can be wrong is by
        // misrepresenting. Picking a winner would assert a national qualifier no single
        // source asserted, and would make the label depend on retrieval order.
        var label = Of(
            Mark(ClassificationLevel.Secret, prefix: "UK"),
            Mark(ClassificationLevel.Secret, prefix: "US"));

        Assert.False(label!.UkPrefix);
        Assert.Equal("SECRET", label.Label);
    }

    [Fact]
    public void Prefix_APrefixedAndAnUnprefixedSourceAlsoDisagree()
    {
        // "No prefix" is a stated position, not an absence of opinion: some content
        // legitimately carries no national qualifier (§21.12). Asserting UK over it would
        // invent a fact about that source.
        var label = Of(
            Mark(ClassificationLevel.Official, prefix: "UK"),
            Mark(ClassificationLevel.Official, prefix: null));

        Assert.False(label!.UkPrefix);
        Assert.Equal("OFFICIAL", label.Label);
    }

    [Fact]
    public void Prefix_UnanimousAbsenceIsAgreement_AndStaysAbsent()
    {
        var label = Of(
            Mark(ClassificationLevel.Official, prefix: null),
            Mark(ClassificationLevel.Secret, ["UK"], prefix: "   ")); // whitespace collapses to none

        Assert.False(label!.UkPrefix);
        Assert.Equal("SECRET UK EYES ONLY", label.Label);
    }

    [Fact]
    public void Prefix_AUnanimousLegacyPrefix_IsNotTheToggle_AndRendersBare()
    {
        // The toggle has two states (§21.12). A legacy prefix that is neither UK nor
        // none - a pre-toggle row a bundle brought across - is not "UK", so the aggregate
        // renders the bare level rather than asserting a qualifier the toggle cannot express.
        var label = Of(
            Mark(ClassificationLevel.Secret, prefix: "NATO"),
            Mark(ClassificationLevel.Secret, prefix: "NATO"));

        Assert.False(label!.UkPrefix);
        Assert.Equal("SECRET", label.Label);
    }

    [Fact]
    public void FailClosedSource_RaisesToTopSecret_AndStripsThePrefix()
    {
        // A page whose marking row went missing contributes ProtectiveMarking.FailClosed:
        // TOP SECRET and deliberately prefixless, because "we do not know what this said"
        // must not assert a national qualifier. Both facts propagate, and the resulting
        // bare "TOP SECRET" is the same quiet visual signal that something is wrong.
        var label = Of(
            ProtectiveMarking.Baseline,
            ProtectiveMarking.FailClosed);

        Assert.Equal(ClassificationLevel.TopSecret, label!.Level);
        Assert.False(label.UkPrefix);
        Assert.Equal("TOP SECRET", label.Label);
    }

    [Fact]
    public void LevelName_IsTheUkWrittenForm_FromTheOneMethod()
    {
        foreach (var level in AllLevels)
        {
            var label = Of(Mark(level));

            Assert.Equal(ProtectiveMarking.LevelName(level), label!.LevelName);
        }
    }

    [Fact]
    public void Equality_ComparesTheCaveatSetsAndSelectorsByValue_NotByReference()
    {
        var a = Of(Mark(ClassificationLevel.Secret, ["UK"], selectors: [Apple]), Mark(ClassificationLevel.Official, ["US"]));
        var b = Of(Mark(ClassificationLevel.Official, ["US"]), Mark(ClassificationLevel.Secret, ["UK"], selectors: [Apple]));
        var c = Of(Mark(ClassificationLevel.Official, ["US"]), Mark(ClassificationLevel.Secret, ["UK"]));

        Assert.Equal(a, b);
        Assert.Equal(a!.GetHashCode(), b!.GetHashCode());
        Assert.NotEqual(a, c);
    }

    // ---------- it cannot gate anything ----------

    [Fact]
    public void AggregateMarkingLabel_IsUnreachableFromEveryCodePathThatDecidesAccess()
    {
        // The proof is structural, not a promise. Every access decision is made in
        // RocketWiki.Core (MarkingGate, EffectivePermissionCalculator) or RocketWiki.Data
        // (PermissionContextLoader and the gated read services). This type lives in
        // RocketWiki.Api, and neither of those assemblies references RocketWiki.Api — so
        // no enforcement code CAN consult it. The same argument that keeps
        // PermissionContextLoader internal to RocketWiki.Data.
        Assert.Equal("RocketWiki.Api", typeof(AggregateMarkingLabel).Assembly.GetName().Name);

        Assembly[] decidingAssemblies =
        [
            typeof(MarkingGate).Assembly,               // RocketWiki.Core
            typeof(PageMarkingReadService).Assembly,    // RocketWiki.Data
        ];

        foreach (var assembly in decidingAssemblies)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name == "RocketWiki.Api");
        }
    }

    [Fact]
    public void AggregateMarkingLabel_AnswersNoAccessQuestion_AndNeverSeesAPrincipal()
    {
        // An access decision needs a principal; this type has never seen one and returns
        // no verdict type. Reflection rather than review, so "just add a Check()" fails
        // the build instead of passing code review.
        var members = typeof(AggregateMarkingLabel)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OfType<MethodBase>()
            .ToArray();

        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, m => m.GetParameters().Any(p => p.ParameterType == typeof(Principal)));

        var verdictTypes = new[] { typeof(EffectivePermission), typeof(PermissionCheckResult), typeof(GateCheck) };
        foreach (var method in members.OfType<MethodInfo>())
        {
            Assert.DoesNotContain(method.ReturnType, verdictTypes);
            Assert.DoesNotContain(method.GetParameters().Select(p => p.ParameterType), t => verdictTypes.Contains(t));
        }
    }
}
