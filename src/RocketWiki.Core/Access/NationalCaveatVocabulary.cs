namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.4: the country tokens an eyes-only caveat may name — the <b>fixed</b> set
/// <c>AUS</c>, <c>CAN</c>, <c>NZ</c>, <c>UK</c>, <c>US</c>. Hard-coded on purpose, and
/// this is a reversal of the earlier design, so the reasoning is stated rather than
/// assumed.
///
/// <para>The previous vocabulary was the registered <c>nationality</c> attribute's allowed
/// values, chosen so that the marking side and the claim side could never drift apart:
/// two admin-editable lists agreeing by convention is how a marking ends up denying the
/// exact audience it names. That argument guarded against <i>two editable sides
/// drifting</i>. With ONE hard-coded vocabulary shared by the marking side, the
/// canonicalizer and the realm contract (§11.5: the <c>nationality</c> mapper must emit
/// these tokens), there is no second side to drift. The only failure left is a mapper
/// emitting a foreign token (<c>GB</c> from an unchanged realm), and
/// <see cref="CanonicalizeKnown"/> turns that into an <i>empty</i> nationality — visible
/// in <c>me.nationality</c>, diagnosable, and denying every caveated page rather than
/// silently matching some — which is the fail-closed reading §6.3 asks for.</para>
///
/// <para><b>What this does and does not filter.</b> The principal side
/// (<see cref="CaveatGate.ResolveNationalities"/>) drops unknown claim values. The
/// marking side does NOT: <see cref="ProtectiveMarking.Create"/> keeps a legacy token
/// such as <c>GB</c> on a row that already carries it, where it matches nobody, and the
/// <c>setPageMarking</c> mutation refuses to write a new one. A marking must read back as
/// the thing that is enforced (§21.4), and a stored token this instance no longer
/// recognises is enforced — against everyone.</para>
///
/// <para>Ordinal order is display order: <c>AUS/NZ EYES ONLY</c>.</para>
/// </summary>
public static class NationalCaveatVocabulary
{
    /// <summary>The five tokens, in canonical (ordinal) order.</summary>
    public static IReadOnlyList<string> Values { get; } = ["AUS", "CAN", "NZ", "UK", "US"];

    private static readonly HashSet<string> Known = new(Values, StringComparer.Ordinal);

    /// <summary>True iff <paramref name="canonical"/> is one of the five. Ordinal: the
    /// caller canonicalizes first (<see cref="ProtectiveMarking.CanonicalizeCountry"/>).</summary>
    public static bool IsKnown(string canonical) => Known.Contains(canonical);

    /// <summary>
    /// The known tokens among <paramref name="values"/>, canonicalized; blanks and
    /// unknown tokens are dropped. This is the principal-side reading — an unrecognised
    /// claim value holds nothing — and it is deliberately not applied to markings (see
    /// the class doc).
    /// </summary>
    public static IReadOnlySet<string> CanonicalizeKnown(IEnumerable<string>? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (values is null)
        {
            return result;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var canonical = ProtectiveMarking.CanonicalizeCountry(value);
            if (Known.Contains(canonical))
            {
                result.Add(canonical);
            }
        }

        return result;
    }
}
