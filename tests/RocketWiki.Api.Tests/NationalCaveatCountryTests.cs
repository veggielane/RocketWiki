using RocketWiki.Api.GraphQL;
using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §21.4: the wire enum on <c>setPageMarking</c>'s <c>eyesOnly</c> and Core's
/// fixed caveat vocabulary are one list in two places. Neither may grow, shrink or
/// re-spell alone: a member the gate does not know would be accepted by validation and
/// stored as a token matching nobody; a Core token the enum lacks would be a caveat no
/// client could ever set.
/// </summary>
public class NationalCaveatCountryTests
{
    [Fact]
    public void TheInputEnum_IsExactlyTheCoreVocabulary_InOrder()
    {
        Assert.Equal(NationalCaveatVocabulary.Values, NationalCaveatCountries.Tokens);
        Assert.All(NationalCaveatCountries.Tokens, token => Assert.True(NationalCaveatVocabulary.IsKnown(token)));
    }

    [Fact]
    public void EveryMember_MapsToItsOwnUpperCaseName()
    {
        // The GraphQL enum value (SCREAMING_SNAKE of the member) and the Core token are the
        // same spelling, so a client reads the token straight off the schema.
        foreach (var member in Enum.GetValues<NationalCaveatCountry>())
        {
            Assert.Equal(member.ToString().ToUpperInvariant(), NationalCaveatCountries.Token(member));
        }
    }
}
