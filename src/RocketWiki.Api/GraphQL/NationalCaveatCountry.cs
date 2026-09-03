using RocketWiki.Core.Access;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The fixed eyes-only vocabulary (design.md §21.4) as a GraphQL enum, on INPUT only:
/// <c>setPageMarking</c> takes its caveat as <c>[NationalCaveatCountry!]!</c>, so a
/// token outside the five is refused by schema validation before any resolver runs, and
/// the enum itself is the vocabulary a client needs — there is no separate query for it.
/// Output stays <c>[String!]!</c>: a legacy token a bundle brought across (<c>GB</c>)
/// still renders and still gates, and an enum on the way out would turn it into a
/// serialization failure on the one row that most needs a reviewer to see it.
///
/// <para>The member names are the Core tokens, and <c>NationalCaveatCountryTests</c> pins
/// the two lists to each other so neither can grow alone.</para>
/// </summary>
public enum NationalCaveatCountry
{
    Aus,
    Can,
    Nz,
    Uk,
    Us,
}

/// <summary>The one mapping from the wire enum to Core's canonical token.</summary>
public static class NationalCaveatCountries
{
    public static string Token(NationalCaveatCountry country) => country switch
    {
        NationalCaveatCountry.Aus => "AUS",
        NationalCaveatCountry.Can => "CAN",
        NationalCaveatCountry.Nz => "NZ",
        NationalCaveatCountry.Uk => "UK",
        NationalCaveatCountry.Us => "US",
        _ => throw new ArgumentOutOfRangeException(nameof(country), country, "Not a national caveat country."),
    };

    /// <summary>Every member's token — what the pin against
    /// <see cref="NationalCaveatVocabulary.Values"/> compares.</summary>
    public static IReadOnlyList<string> Tokens { get; } =
        Enum.GetValues<NationalCaveatCountry>().Select(Token).ToArray();
}
