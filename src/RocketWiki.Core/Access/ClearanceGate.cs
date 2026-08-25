using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21: decides whether a principal's clearance admits a page's protective
/// marking. Pure, in-process, and deliberately tiny — this is the only place a
/// classification decision is made, and
/// <see cref="EffectivePermissionCalculator.Compute"/> is the only caller that matters,
/// which is what makes "classification composes by subtraction everywhere" a structural
/// property rather than a rule each read path has to remember.
///
/// <para><b>It can only ever subtract.</b> Nothing in this class returns "allow" in a
/// case the surrounding permission computation would otherwise have denied; it is
/// AND-ed onto an already-computed canView. A space grant, a passing page restriction,
/// and an instance-admin role are all upstream of it and none of them is visible from
/// here — there is nothing in this file for a caller to bypass with, the same posture
/// <see cref="EffectivePermissionCalculator"/> takes toward instance admins (§6.5).</para>
/// </summary>
public static class ClearanceGate
{
    /// <summary>
    /// The well-known key in <c>Principal.Attributes</c> carrying the caller's
    /// clearance, registered in the attribute registry (§6.2) like any other
    /// rule-usable attribute. Expected claim values are the four
    /// <see cref="ClassificationLevel"/> member names in SCREAMING_SNAKE form:
    /// <c>OFFICIAL</c>, <c>OFFICIAL_SENSITIVE</c>, <c>SECRET</c>, <c>TOP_SECRET</c>.
    /// </summary>
    public const string ClearanceAttributeKey = "clearance";

    /// <summary>
    /// The well-known key carrying the caller's nationality — the SAME attribute §6.2
    /// already describes and the rule engine's <c>attr</c> conditions already match on.
    /// The eyes-only caveat is enforced against this and nothing else.
    /// </summary>
    public const string NationalityAttributeKey = "nationality";

    /// <summary>The reason prefix for a level failure. Collapsed to the bounded metric
    /// category <c>classification</c> by <c>CoreTelemetry.CategorizeDenialReason</c>.</summary>
    public const string ClassificationReasonPrefix = "classification:";

    /// <summary>
    /// The reason for a caveat failure. Deliberately carries NO country list: the
    /// specific set belongs in the audit row, and a reason string that named the
    /// countries would put the marking's contents one careless tag away from a metric
    /// dimension (design.md §15).
    /// </summary>
    public const string EyesOnlyReason = "caveat:eyes_only";

    /// <summary>
    /// The principal's clearance, resolved from the registered <c>clearance</c>
    /// attribute.
    ///
    /// <para><b>Absent, unrecognised, or malformed grants OFFICIAL — and only
    /// OFFICIAL.</b> This is the fail-closed reading, and it is a deliberate middle
    /// rather than either extreme. "No clearance = see everything" is obviously wrong.
    /// "No clearance = see nothing" is wrong too, in a subtler way: it would make an
    /// unconfigured claim mapper silently empty the entire wiki for every user, which is
    /// an outage dressed up as security and, worse, an outage that pressures whoever is
    /// on call into turning the check off. Granting the least sensitive tier denies
    /// everything above OFFICIAL — which is the whole point of the control — while
    /// leaving OFFICIAL content readable, exactly as it was before markings existed.
    /// It matches the engine's existing doctrine that a missing attribute matches no
    /// condition (<c>Attr_MissingAttribute_FailsClosed</c>): the principal gets nothing
    /// from the attribute, and OFFICIAL is what "nothing" is worth.</para>
    ///
    /// <para>A multi-valued clearance claim takes the HIGHEST recognised value, mirroring
    /// §6.4's "your role is the highest whose expression you satisfy". Unrecognised
    /// values are ignored rather than poisoning the result, so a garbage extra value can
    /// never raise clearance and can never lower it below the OFFICIAL floor. Matching
    /// is ordinal and exact, per §6.3.</para>
    /// </summary>
    public static ClassificationLevel ResolveClearance(Principal principal)
    {
        if (!principal.Attributes.TryGetValue(ClearanceAttributeKey, out var values))
        {
            return ClassificationLevel.Official;
        }

        var best = ClassificationLevel.Official;
        foreach (var value in values)
        {
            if (TryParseLevel(value, out var level) && level > best)
            {
                best = level;
            }
        }

        return best;
    }

    /// <summary>
    /// Parses a claim value into a level. Ordinal, exact, and closed: only the four
    /// SCREAMING_SNAKE member names parse. <c>Enum.TryParse</c> is deliberately NOT used
    /// — it accepts the C# member spellings, is case-insensitive on request, and (worst)
    /// happily parses <c>"4"</c> into <see cref="ClassificationLevel.TopSecret"/>, which
    /// would let a numeric claim value grant the top of the ladder.
    /// </summary>
    public static bool TryParseLevel(string? value, out ClassificationLevel level)
    {
        switch (value)
        {
            case "OFFICIAL":
                level = ClassificationLevel.Official;
                return true;
            case "OFFICIAL_SENSITIVE":
                level = ClassificationLevel.OfficialSensitive;
                return true;
            case "SECRET":
                level = ClassificationLevel.Secret;
                return true;
            case "TOP_SECRET":
                level = ClassificationLevel.TopSecret;
                return true;
            default:
                level = ClassificationLevel.Official;
                return false;
        }
    }

    /// <summary>
    /// The principal's nationality values in canonical form (see
    /// <see cref="ProtectiveMarking.CanonicalizeCountry"/>). Empty when the attribute is
    /// absent or holds nothing usable — which denies every non-empty eyes-only set.
    /// </summary>
    public static IReadOnlyCollection<string> ResolveNationalities(Principal principal)
    {
        if (!principal.Attributes.TryGetValue(NationalityAttributeKey, out var values))
        {
            return [];
        }

        var canonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                canonical.Add(ProtectiveMarking.CanonicalizeCountry(value));
            }
        }

        return canonical;
    }

    /// <summary>
    /// The decision. Allows only when BOTH hold:
    /// <list type="bullet">
    /// <item>the principal's clearance is at or above the marking's level; and</item>
    /// <item>the eyes-only set is empty, or the principal holds at least one nationality
    /// value in it (a non-empty set intersection).</item>
    /// </list>
    ///
    /// <para>An absent or empty nationality attribute therefore DENIES any page carrying
    /// a caveat — fail closed, consistent with <c>AttrCondition</c>: a principal with no
    /// value for an attribute matches no condition that tests it, and the caveat is a
    /// condition on nationality.</para>
    ///
    /// <para>Both checks run in the order that reports the more fundamental failure
    /// first: a caller who lacks the level is not told (via the audit row) that they also
    /// lack the nationality, because the level is the coarser fact and naming one reason
    /// per denial is what makes §7's reasons stable.</para>
    /// </summary>
    public static PermissionCheckResult Check(ProtectiveMarking marking, Principal principal)
    {
        if (ResolveClearance(principal) < marking.Level)
        {
            return PermissionCheckResult.Deny(ClassificationReasonPrefix + ProtectiveMarking.LevelToken(marking.Level));
        }

        if (!marking.HasEyesOnly)
        {
            return PermissionCheckResult.Allow();
        }

        var held = ResolveNationalities(principal);
        foreach (var country in marking.EyesOnly)
        {
            if (held.Contains(country))
            {
                return PermissionCheckResult.Allow();
            }
        }

        return PermissionCheckResult.Deny(EyesOnlyReason);
    }
}
