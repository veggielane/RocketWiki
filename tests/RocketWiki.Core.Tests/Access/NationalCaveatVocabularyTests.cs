using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.4: the caveat vocabulary is the fixed five-eyes set, hard-coded on both
/// sides. These pin the set itself and the principal-side canonicalization that turns a
/// foreign mapper token into an EMPTY nationality rather than a partial match.
/// </summary>
public class NationalCaveatVocabularyTests
{
    [Fact]
    public void Values_AreExactlyTheFiveEyes_InOrdinalOrder()
    {
        // Ordinal order is display order (AUS/NZ EYES ONLY), and the set is the whole
        // vocabulary: adding a token here is a design change to §21.4 and §11.5's realm
        // contract, not a tidy-up.
        Assert.Equal(["AUS", "CAN", "NZ", "UK", "US"], NationalCaveatVocabulary.Values);
        Assert.All(NationalCaveatVocabulary.Values, v => Assert.True(NationalCaveatVocabulary.IsKnown(v)));
    }

    [Theory]
    [InlineData("GB")]     // the legacy alias the old registry-based vocabulary allowed
    [InlineData("uk")]     // IsKnown is ordinal on the CANONICAL form; the caller canonicalizes first
    [InlineData("GBR")]
    [InlineData("")]
    public void IsKnown_RefusesAnythingOutsideTheSet(string token) => Assert.False(NationalCaveatVocabulary.IsKnown(token));

    [Fact]
    public void CanonicalizeKnown_DropsUnknownAndCases()
    {
        var held = NationalCaveatVocabulary.CanonicalizeKnown(["gb", " uk ", "US", "GBR", "", "  ", "nz"]);

        Assert.Equal(["NZ", "UK", "US"], held.OrderBy(v => v, StringComparer.Ordinal));
        // GB is NOT aliased to UK: a mapper emitting GB holds nothing, which is visible
        // (me.nationality) and fails closed rather than silently matching.
        Assert.DoesNotContain("GB", held);
    }

    [Fact]
    public void CanonicalizeKnown_NullOrOnlyUnknown_IsEmpty()
    {
        Assert.Empty(NationalCaveatVocabulary.CanonicalizeKnown(null));
        Assert.Empty(NationalCaveatVocabulary.CanonicalizeKnown(["GB", "FR"]));
    }
}
