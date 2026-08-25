namespace RocketWiki.Core.Enums;

/// <summary>
/// design.md §21: the UK Government protective-marking scheme, as a fixed and totally
/// ordered ladder. Every page carries exactly one of these; a principal's clearance is
/// resolved to one of these; and a page is viewable only when the clearance is
/// numerically greater than or equal to the marking.
///
/// <para><b>The numeric values are load-bearing.</b> The ordering IS the comparison —
/// <c>clearance &gt;= level</c> — so a member inserted in the middle would silently
/// re-rank the whole scheme. The scheme is fixed by policy, not extensible by
/// configuration, which is exactly why hard-coding the order here is safe.</para>
///
/// <para><b>Numbering starts at 1, deliberately.</b> <c>default(ClassificationLevel)</c>
/// is 0 and therefore not a valid level, so a struct that was never initialized cannot
/// read as OFFICIAL and quietly let content through. Anything that needs a "we don't
/// know" value must fail closed explicitly (see
/// <see cref="RocketWiki.Core.Access.ProtectiveMarking.FailClosed"/>), never by
/// defaulting.</para>
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
