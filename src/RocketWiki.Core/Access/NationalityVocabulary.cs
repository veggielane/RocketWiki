using System.Text.Json;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21: the set of country values an eyes-only caveat may be built from. It is
/// the <b>allowed values of the registered <c>nationality</c> attribute</b> (§6.2) — the
/// instance's own vocabulary — and it is emphatically NOT an ISO 3166 list.
///
/// <para><b>Why that distinction is load-bearing, and why "simplifying" it into an ISO
/// list is a security bug rather than a tidy-up.</b> Enforcement compares the marking's
/// country set against the principal's <c>nationality</c> claim values. Those claim
/// values are whatever this instance's Keycloak protocol mapper emits and whatever its
/// admins registered — they might be <c>GB</c>, they might be <c>UK</c>, they might be
/// <c>GBR</c> or something site-specific. If the marking side were populated from a
/// hard-coded ISO list, an admin could pick <c>GB</c> on a wiki whose tokens say
/// <c>UK</c>, and every comparison would fail. The failure mode is the dangerous kind:
/// it fails <i>closed</i>, so nothing looks broken — the page simply becomes invisible to
/// everybody, including the people it was released to, and the marking looks perfectly
/// correct in the admin UI while denying the exact audience it names. Drawing both sides
/// from one registry makes that class of mismatch unrepresentable.</para>
///
/// <para>Consequence, stated plainly rather than worked around: <b>if no
/// <c>nationality</c> attribute is registered, or it declares no allowed values, an
/// eyes-only set cannot be set at all.</b> There is nothing to pick from, and inventing a
/// vocabulary would produce a caveat that can never match anyone. The refusal is a
/// <c>ValidationError</c> naming the attribute, so the fix is obvious and
/// administrative.</para>
/// </summary>
public static class NationalityVocabulary
{
    /// <summary>
    /// The canonical (upper-cased, trimmed) allowed values declared by the nationality
    /// attribute, or an empty set when the attribute declares none. A malformed or
    /// non-array <c>AllowedValuesJson</c> yields an empty set rather than throwing —
    /// fail closed, exactly as a malformed rule expression denies (§6.3): an
    /// unparseable vocabulary means no country can be validated, which means no
    /// eyes-only set can be created, which is the safe end of the failure.
    /// </summary>
    public static IReadOnlySet<string> Parse(string? allowedValuesJson)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(allowedValuesJson))
        {
            return values;
        }

        try
        {
            using var document = JsonDocument.Parse(allowedValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    values.Add(ProtectiveMarking.CanonicalizeCountry(value));
                }
            }
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return values;
    }
}
