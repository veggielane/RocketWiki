using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21: the two principal-attribute gates a protective marking imposes — the
/// clearance against the level (C, §21.3) and the nationality against the eyes-only
/// caveat (N, §21.4). Pure, in-process, and deliberately tiny — these are the only places
/// a classification or caveat decision is made, and <see cref="MarkingGate"/> is the only
/// caller that matters: it composes them with the selector gates in the one reporting
/// order, and <see cref="EffectivePermissionCalculator.Compute"/> runs that composition on
/// every read path. That is what makes "a marking composes by subtraction everywhere" a
/// structural property rather than a rule each read path has to remember.
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
    /// The eyes-only caveat is enforced against this and nothing else. The values the
    /// caveat recognises are <see cref="NationalCaveatVocabulary"/>'s five; §11.5 requires
    /// the realm's mapper to emit exactly those.
    /// </summary>
    public const string NationalityAttributeKey = "nationality";

    /// <summary>
    /// What an absent, unrecognised or malformed clearance is worth: <b>OFFICIAL-SENSITIVE</b>
    /// (design.md §21.3). A deliberate middle, not a compromise. "No clearance = see
    /// everything" is obviously wrong. "No clearance = see nothing" is wrong in a subtler
    /// way: an unconfigured claim mapper would empty the entire wiki for every user, which
    /// is an outage dressed up as security and, worse, an outage that pressures whoever is
    /// on call into turning the check off. OFFICIAL-SENSITIVE is the everyday working
    /// tier of an organisation whose routine content is sensitive by default — the level
    /// most pages are actually written at — so it is what "nothing" is worth: SECRET and
    /// above stay denied, which is the whole point of the control, while everyday content
    /// reads exactly as it did before markings existed. Stated once here and read by the
    /// resolver and by <c>me</c>'s anonymous floor, so the gate and the affordance cannot
    /// drift.
    /// </summary>
    public const ClassificationLevel DefaultClearance = ClassificationLevel.OfficialSensitive;

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
    /// <para><b>Absent, unrecognised, or malformed grants <see cref="DefaultClearance"/>
    /// — and only that.</b> See the constant for why that floor is neither extreme. It
    /// matches the engine's existing doctrine that a missing attribute matches no
    /// condition (<c>Attr_MissingAttribute_FailsClosed</c>): the principal gets nothing
    /// from the attribute, and the floor is what "nothing" is worth.</para>
    ///
    /// <para><b>A recognised claim is honoured even below the floor.</b> The floor is what
    /// an absent or garbage claim is worth, not a minimum anyone is promoted to: a realm
    /// that says <c>OFFICIAL</c> has stated a fact about the principal, and rounding it up
    /// would widen what the token asserted — the wrong direction to err in. It also keeps
    /// the bottom tier of the ladder meaningful as a clearance rather than only as a
    /// marking level.</para>
    ///
    /// <para>A multi-valued clearance claim takes the HIGHEST recognised value, mirroring
    /// §6.4's "your role is the highest whose expression you satisfy". Unrecognised
    /// values are ignored rather than poisoning the result, so a garbage extra value can
    /// never raise clearance; a claim holding only garbage is worth the floor. Matching
    /// is ordinal and exact, per §6.3.</para>
    /// </summary>
    public static ClassificationLevel ResolveClearance(Principal principal)
    {
        if (!principal.Attributes.TryGetValue(ClearanceAttributeKey, out var values))
        {
            return DefaultClearance;
        }

        ClassificationLevel? best = null;
        foreach (var value in values)
        {
            if (TryParseLevel(value, out var level) && (best is null || level > best.Value))
            {
                best = level;
            }
        }

        return best ?? DefaultClearance;
    }

    /// <summary>
    /// Parses a claim value into a level. Ordinal, exact, and closed: only the four
    /// SCREAMING_SNAKE member names parse. <c>Enum.TryParse</c> is deliberately NOT used
    /// — it accepts the C# member spellings, is case-insensitive on request, and (worst)
    /// happily parses <c>"4"</c> into <see cref="ClassificationLevel.TopSecret"/>, which
    /// would let a numeric claim value grant the top of the ladder. The failure
    /// <paramref name="level"/> is the floor, so a caller that ignores the return value
    /// still cannot read above it.
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
                level = DefaultClearance;
                return false;
        }
    }

    /// <summary>
    /// The principal's nationality values that the caveat recognises, canonical (see
    /// <see cref="NationalCaveatVocabulary.CanonicalizeKnown"/>). Empty when the
    /// attribute is absent, holds nothing usable, or holds only tokens outside the fixed
    /// set — which denies every non-empty eyes-only set. A mapper still emitting
    /// <c>GB</c> therefore yields a principal who holds <i>nothing</i>, visibly (the
    /// <c>me</c> query echoes this set), rather than one who silently half-matches.
    /// </summary>
    public static IReadOnlyCollection<string> ResolveNationalities(Principal principal) =>
        principal.Attributes.TryGetValue(NationalityAttributeKey, out var values)
            ? NationalCaveatVocabulary.CanonicalizeKnown(values)
            : [];

    /// <summary>
    /// The two principal-attribute gates together, C then N. Allows only when BOTH hold:
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
    ///
    /// <para><b>Not the gate the product runs.</b> A marking also carries selectors, and
    /// the composition every read path uses is <see cref="MarkingGate.Check"/>, which
    /// places the selector gates between these two. This method remains for the
    /// caveat-only ladder tests and for callers that have proven their marking carries no
    /// selectors; nothing in the data layer calls it, and a source-level test keeps it
    /// that way.</para>
    /// </summary>
    public static PermissionCheckResult Check(ProtectiveMarking marking, Principal principal)
    {
        var classification = CheckClassification(marking, principal);
        return classification.IsAllowed ? CheckCaveat(marking, principal) : classification;
    }

    /// <summary>
    /// The level half alone (C): clearance at or above the marking's level. Split out so
    /// <see cref="MarkingGate"/> can place the selector gates between the level and the
    /// caveat in its reporting order (§21.2); <see cref="Check"/> is exactly this followed
    /// by <see cref="CheckCaveat"/>.
    /// </summary>
    public static PermissionCheckResult CheckClassification(ProtectiveMarking marking, Principal principal) =>
        ResolveClearance(principal) < marking.Level
            ? PermissionCheckResult.Deny(ClassificationReasonPrefix + ProtectiveMarking.LevelToken(marking.Level))
            : PermissionCheckResult.Allow();

    /// <summary>
    /// The caveat half alone (N): the eyes-only set is empty, or the principal holds at
    /// least one nationality in it. See <see cref="Check"/> for the fail-closed reading
    /// of an absent nationality.
    /// </summary>
    public static PermissionCheckResult CheckCaveat(ProtectiveMarking marking, Principal principal)
    {
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
