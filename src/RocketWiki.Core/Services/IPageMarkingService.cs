using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// Setting a page's protective marking (design.md §21). Reading one needs no service:
/// the marking is resolved on a <c>Page</c> that already passed object-level
/// authorization, exactly like labels and properties (§6.4.2/§20), so it rides a
/// DataLoader off the page rather than a gated read path of its own.
///
/// <para><b>Permissions.</b> Setting requires <c>canEdit</c> on that page — a marking is
/// that page's metadata, the same call as attaching a label — sitting beneath the replica
/// invariant (§12), which refuses first and beneath every grant.</para>
///
/// <para><b>You may not set a marking you could not then read.</b> This is the one rule
/// here that is not a restatement of §6: the resulting marking must pass the caller's own
/// clearance gate. Its rationale is literal — classifying a page above your clearance
/// makes the page instantly invisible to you, so the "edit" would consume a canEdit
/// authorization to produce something you can no longer see, and the only way back is to
/// find someone cleared higher. Enforcing the resulting marking as a whole (rather than
/// just its level) is what mechanizes that rationale: marking a page
/// <c>SECRET [US EYES ONLY]</c> as a GB-national editor loses the page just as
/// completely as over-classifying it does.</para>
///
/// <para><b>The eyes-only vocabulary is the nationality attribute's</b>, not ISO 3166 —
/// see <c>PageMarkingService</c> for why that distinction is the difference between a
/// working control and one that silently denies everyone.</para>
/// </summary>
public interface IPageMarkingService
{
    /// <summary>
    /// Sets the whole marking (level and eyes-only set together — a marking is one
    /// value, and a partial update would let a caller change the level without ever
    /// stating what caveat they meant). Idempotent: setting the marking a page already
    /// carries succeeds and audits as an ordinary <c>page.marking.set</c>.
    /// </summary>
    Task<PageMutationResult<PageMarkingView>> SetAsync(
        SetPageMarkingRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="EyesOnly"/> is the full replacement set — empty clears the caveat.
/// Values are validated against the registered <c>nationality</c> attribute's allowed
/// values and stored canonical (see <c>ProtectiveMarking.CanonicalizeCountry</c>).
/// </summary>
public sealed record SetPageMarkingRequest(Guid PageId, ClassificationLevel Level, IReadOnlyList<string> EyesOnly);

/// <summary>
/// One page's marking, flattened for callers, with the display string built server-side
/// so every surface — the SPA, an MCP client, an audit reviewer reading the details —
/// renders the identical text. Two renderings of one marking that disagree is a
/// compliance problem, so <c>Label</c> has exactly one implementation
/// (<c>ProtectiveMarking.Format</c>) and it is this one.
///
/// <para>Deliberately not the <c>PageMarking</c> entity: that carries a <c>Page</c>
/// navigation, and returning it from a read path would open a Page-shaped route around
/// the object-level authorization every such field goes through — the same reasoning
/// that produced <c>LabelRef</c> and <c>PagePropertyValue</c>.</para>
/// </summary>
public sealed record PageMarkingView(ClassificationLevel Level, IReadOnlyList<string> EyesOnly, string Label)
{
    public static PageMarkingView From(ProtectiveMarking marking) =>
        new(marking.Level, marking.EyesOnly, marking.Format());
}
