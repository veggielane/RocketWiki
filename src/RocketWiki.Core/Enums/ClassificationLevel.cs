namespace RocketWiki.Core.Enums;

/// <summary>
/// design.md §21: the UK Government protective-marking scheme, as a fixed and totally
/// ordered ladder. Every page carries exactly one of these.
///
/// <para><b>The ordering is for DISPLAY and for §21.13's aggregate maximum only — it is
/// not an access comparison.</b> A principal's clearance used to be resolved to one of
/// these and compared numerically against a page's level; that gate is gone, because this
/// deployment carries no clearance attribute in Keycloak to resolve from, and the level
/// is now presentational like the national prefix (§21.12). What the numeric values still
/// decide: the order <c>classificationScheme</c> lists the ladder in, and which of several
/// sources' levels an aggregate label (a search result, an Ask answer, a tree payload)
/// leads with. A member inserted in the middle would re-rank both, so the scheme stays
/// fixed by policy, not extensible by configuration — which is exactly why hard-coding
/// the order here is safe.</para>
///
/// <para><b>Numbering starts at 1, deliberately.</b> <c>default(ClassificationLevel)</c>
/// is 0 and therefore not a valid level, so a struct that was never initialized cannot
/// read as OFFICIAL and quietly render content as less marked than it is. Anything that
/// needs a "we don't know" value must say so explicitly (see
/// <see cref="RocketWiki.Core.Access.ProtectiveMarking.FailClosed"/>, which denies by
/// its own flag rather than by its level), never by defaulting.</para>
///
/// <para>The wire format for sync (§12) and the GraphQL enum are both this member's
/// NAME, so the numeric values only have to stay stable within one instance's own
/// database.</para>
/// </summary>
public enum ClassificationLevel : byte
{
    Official = 1,
    OfficialSensitive = 2,
    Secret = 3,
    TopSecret = 4,
}
