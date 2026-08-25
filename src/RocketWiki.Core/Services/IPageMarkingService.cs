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
/// completely as over-classifying it does. <b>The prefix is outside that rule</b> and
/// falls outside it for free rather than by exception: the check is
/// <c>ClearanceGate.Check(resultingMarking, principal)</c>, and the gate does not read
/// the prefix, so there is no prefix a caller can be refused for (design.md §21.12).</para>
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
///
/// <para><paramref name="Prefix"/> is the national qualifier (design.md §21.12),
/// upper-cased and trimmed on write. It is <b>optional and clearable</b>: omitting it
/// keeps the instance default (<c>UK</c>), and passing null or an empty string removes
/// it, which renders the bare level. Unlike the level and the caveat it is validated
/// against nothing but its length, because it grants and denies nothing.</para>
/// </summary>
public sealed record SetPageMarkingRequest(
    Guid PageId,
    ClassificationLevel Level,
    IReadOnlyList<string> EyesOnly,
    string? Prefix = ProtectiveMarking.DefaultPrefix);

/// <summary>
/// One page's marking, flattened for callers, with the display strings built server-side
/// so every surface — the SPA, an MCP client, an audit reviewer reading the details —
/// renders identical text. Two renderings of one marking that disagree is a compliance
/// problem, so <c>Label</c> has exactly one implementation
/// (<c>ProtectiveMarking.Format</c>) and it is this one.
///
/// <para><b>Two display strings, and choosing wrongly matters.</b> <c>Label</c> is the
/// WHOLE marking — prefix, level, caveat (<c>UK SECRET [GB EYES ONLY]</c>) — and is what
/// anything claiming to show "this page's marking" must render. <c>LevelName</c> is the
/// level ALONE in its UK written form (<c>OFFICIAL-SENSITIVE</c>, <c>TOP SECRET</c>), for
/// the surfaces that have nowhere to put a full marking: a one-word list badge, a radio
/// option in a picker. It exists because otherwise a client would transliterate the
/// GraphQL enum itself — <c>OFFICIAL_SENSITIVE</c> is a machine identifier, not a
/// marking — and §21.1's "one method per spelling, on the server, so they cannot drift"
/// would be quietly broken by the one consumer that matters.</para>
///
/// <para>Rendering <c>LevelName</c> where <c>Label</c> belongs understates the marking:
/// it drops the caveat, so a page released only to GB nationals would read as plain
/// SECRET. That direction is the safer error (the caveat is still *enforced* regardless
/// — the badge is informational, not the control), but it is still wrong, and it is why
/// the fields are named to be hard to confuse rather than <c>label</c>/<c>levelLabel</c>.
/// <c>LevelName</c> maps 1:1 onto <c>ProtectiveMarking.LevelName</c>, so its provenance
/// is one grep away.</para>
///
/// <para>Deliberately not the <c>PageMarking</c> entity: that carries a <c>Page</c>
/// navigation, and returning it from a read path would open a Page-shaped route around
/// the object-level authorization every such field goes through — the same reasoning
/// that produced <c>LabelRef</c> and <c>PagePropertyValue</c>.</para>
/// </summary>
public sealed record PageMarkingView(
    ClassificationLevel Level, IReadOnlyList<string> EyesOnly, string? Prefix, string Label, string LevelName)
{
    public static PageMarkingView From(ProtectiveMarking marking) =>
        new(
            marking.Level,
            marking.EyesOnly,
            marking.Prefix,
            marking.Format(),
            ProtectiveMarking.LevelName(marking.Level));

    // No instance methods here, deliberately: this record IS a GraphQL object type, and
    // Hot Chocolate infers a field from every public instance member. An instance helper
    // would silently grow the published schema (and, for anything returning
    // ProtectiveMarking, drag a second representation of a marking into the SDL beside
    // this one). The inverse of From lives as an extension method in the API layer, where
    // it is needed - see RocketWiki.Api.Markings.PageMarkingViewExtensions.
}
