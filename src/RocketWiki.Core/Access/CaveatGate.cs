namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.4: the one principal-attribute gate a protective marking imposes — the
/// nationality against the eyes-only caveat (N). Pure, in-process, and deliberately
/// tiny — this is the only place a caveat decision is made, and <see cref="MarkingGate"/>
/// is the only caller that matters: it composes this with the selector gate in the one
/// reporting order, and <see cref="EffectivePermissionCalculator.Compute"/> runs that
/// composition on every read path. That is what makes "a marking composes by subtraction
/// everywhere" a structural property rather than a rule each read path has to remember.
///
/// <para><b>This class used to be <c>ClearanceGate</c></b>, and carried a second gate:
/// the principal's clearance attribute against the marking's level (C). That gate is
/// gone, and so is the name — a class still called ClearanceGate with no clearance in
/// it would have been exactly the drift this codebase refuses. It went because this
/// deployment carries no per-user clearance attribute in Keycloak at all: a gate that
/// compared the level against a claim nobody emits would have denied everyone on a
/// made-up floor (or, worse, defaulted everyone to a floor and called it a control).
/// The level is now presentational, like the prefix (§21.12), and the only
/// principal-side fact a marking is compared against is nationality. Nothing about
/// nationality changed: §11.5 still requires the realm's mapper to emit
/// <see cref="NationalCaveatVocabulary"/>'s five tokens, and an absent or foreign value
/// still holds nothing.</para>
///
/// <para><b>It can only ever subtract.</b> Nothing in this class returns "allow" in a
/// case the surrounding permission computation would otherwise have denied; it is
/// AND-ed onto an already-computed canView. A space grant, a passing page restriction,
/// and an instance-admin role are all upstream of it and none of them is visible from
/// here — there is nothing in this file for a caller to bypass with, the same posture
/// <see cref="EffectivePermissionCalculator"/> takes toward instance admins (§6.5).</para>
/// </summary>
public static class CaveatGate
{
    /// <summary>
    /// The well-known key carrying the caller's nationality — the SAME attribute §6.2
    /// already describes and the rule engine's <c>attr</c> conditions already match on.
    /// The eyes-only caveat is enforced against this and nothing else. The values the
    /// caveat recognises are <see cref="NationalCaveatVocabulary"/>'s five; §11.5 requires
    /// the realm's mapper to emit exactly those.
    /// </summary>
    public const string NationalityAttributeKey = "nationality";

    /// <summary>
    /// The reason for a caveat failure. Deliberately carries NO country list: the
    /// specific set belongs in the audit row, and a reason string that named the
    /// countries would put the marking's contents one careless tag away from a metric
    /// dimension (design.md §15).
    /// </summary>
    public const string EyesOnlyReason = "caveat:eyes_only";

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
    /// The caveat gate (N): allows when the eyes-only set is empty, or the principal
    /// holds at least one nationality value in it (a non-empty set intersection).
    ///
    /// <para>An absent or empty nationality attribute therefore DENIES any page carrying
    /// a caveat — fail closed, consistent with <c>AttrCondition</c>: a principal with no
    /// value for an attribute matches no condition that tests it, and the caveat is a
    /// condition on nationality.</para>
    ///
    /// <para><b>Not the gate the product runs.</b> A marking also carries selectors, and
    /// the composition every read path uses is <see cref="MarkingGate.Check"/>, which
    /// places the selector gate before this one. This method remains for the caveat
    /// ladder tests and for callers that have proven their marking carries no selectors;
    /// nothing in the data layer calls it, and a source-level test keeps it that
    /// way.</para>
    /// </summary>
    public static PermissionCheckResult Check(ProtectiveMarking marking, Principal principal)
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
