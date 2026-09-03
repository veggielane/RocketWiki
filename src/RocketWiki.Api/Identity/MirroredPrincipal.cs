using System.Text.Json;
using RocketWiki.Core.Access;

namespace RocketWiki.Api.Identity;

/// <summary>
/// Rebuilds a <see cref="Principal"/> from a <c>User</c> row's mirrored attributes
/// (<c>AttributesJson</c>, written by <see cref="JitUserProvisioningMiddleware"/>) so the
/// clearance and selector gates' own resolution functions —
/// <see cref="ClearanceGate.ResolveClearance"/>, <see cref="SelectorGate.ResolveEligibleCategories"/>
/// — can run over it unchanged. This is what the profile page (design.md §6.2) renders
/// from: "the clearance and eligibility the gate would have derived from the claims
/// recorded at this person's last request".
///
/// <para><b>It exists so that there is no second implementation of the rules.</b> A
/// profile that said "eligible" where the gate would say "not" — because it compared
/// <c>yes</c> case-sensitively, or parsed the clearance with <c>Enum.TryParse</c> and
/// accepted <c>"4"</c> as TOP SECRET (§21.3) — would be a lie about the enforcement, and
/// exactly the drift <c>me</c> already refuses by resolving through the gates. Nothing
/// here interprets a value: the mirror's strings become attribute values as-is, and the
/// gates decide what they are worth.</para>
///
/// <para><b>Same allowlist as <see cref="PrincipalBuilder"/>, same absence rule.</b> The
/// keys mapped are nationality, clearance, and every claim the CURRENT catalog names —
/// a key the mirror holds for a category since removed from configuration is ignored,
/// and a category added since the user's last sign-in reads as "no claim recorded",
/// which the gate reads as not eligible. A key whose list is empty is absent from the
/// principal, not present-and-empty, matching the builder's contract that "absent means
/// the floor" and "absent matches nothing". Groups are always empty: the mirror never
/// recorded them, and nothing that reads this principal asks a group question.</para>
///
/// <para><b>Malformed JSON fails closed</b>: a row whose mirror cannot be parsed yields a
/// principal with no attributes — the floor clearance and no gated eligibility — rather
/// than an error that would turn a profile read into a 500 or, worse, a guess.</para>
///
/// <para><b>Never an authorization input.</b> design.md §6.1 forbids reading the mirror
/// for a decision, and this class does not change that: the principal it returns is for
/// rendering a fact about a mirror, and no gate on any read or write path is ever handed
/// it. The one caller is <c>Query.userProfile</c>.</para>
/// </summary>
public static class MirroredPrincipal
{
    /// <summary>
    /// Builds the principal. <paramref name="userId"/> is the mirrored subject (or, for a
    /// shadow user that has none, any stable identifier — nothing that reads the result
    /// consults it); <paramref name="attributesJson"/> is the row's mirror as written by
    /// JIT provisioning: a JSON object of string lists keyed by claim name.
    /// </summary>
    public static Principal Build(string userId, string? attributesJson, SelectorCatalog catalog)
    {
        var mirror = Parse(attributesJson);

        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        AddIfPresent(attributes, mirror, ClearanceGate.NationalityAttributeKey);
        AddIfPresent(attributes, mirror, ClearanceGate.ClearanceAttributeKey);
        foreach (var claimName in catalog.ClaimNames)
        {
            AddIfPresent(attributes, mirror, claimName);
        }

        return Principal.Create(userId, [], attributes.Count > 0 ? attributes : null);
    }

    private static Dictionary<string, string[]> Parse(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string[]>>(attributesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddIfPresent(
        List<KeyValuePair<string, IReadOnlyList<string>>> attributes,
        Dictionary<string, string[]> mirror,
        string key)
    {
        // A null element cannot have come from a claim (Claim.Value is never null); it can
        // only be a hand-edited or corrupted mirror, and dropping it is the fail-closed
        // reading — the gates treat "no value" as the floor / not eligible.
        if (mirror.TryGetValue(key, out var values) && values is not null)
        {
            var present = values.Where(v => v is not null).ToArray();
            if (present.Length > 0)
            {
                attributes.Add(new KeyValuePair<string, IReadOnlyList<string>>(key, present));
            }
        }
    }
}
