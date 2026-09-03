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
/// <c>SECRET US EYES ONLY</c> as a UK-national editor, or asserting a selector this
/// space never granted them, loses the page just as completely as over-classifying it
/// does. <b>The prefix is outside that rule</b> and falls outside it for free rather than
/// by exception: the check is <c>MarkingGate.Check(resultingMarking, principal, catalog,
/// grantedSelectors)</c> - the one composition every read path uses (§21.2) - and no gate
/// in it reads the prefix, so there is no prefix a caller can be refused for (design.md
/// §21.12).</para>
///
/// <para><b>The eyes-only vocabulary is the fixed five-eyes set</b>
/// (<c>NationalCaveatVocabulary</c>, design.md §21.4); a value outside it is refused with
/// a <c>ValidationError</c>, because a caveat naming a token the realm never emits would
/// match nobody while looking correct.</para>
/// </summary>
public interface IPageMarkingService
{
    /// <summary>
    /// Sets the whole marking (level, selectors, eyes-only set and prefix together — a
    /// marking is one value, and a partial update would let a caller change the level
    /// without ever stating what caveat or compartments they meant). Idempotent: setting
    /// the marking a page already carries succeeds and audits as an ordinary
    /// <c>page.marking.set</c>.
    /// </summary>
    Task<PageMutationResult<PageMarkingView>> SetAsync(
        SetPageMarkingRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}

/// <summary>
/// The whole marking, stated in full (design.md §21.6): a marking is one value, so every
/// part is a replacement, never a patch.
///
/// <para><paramref name="EyesOnly"/> is the full replacement caveat set — empty clears
/// it. Values are validated against the fixed <c>NationalCaveatVocabulary</c> and stored
/// canonical (see <c>ProtectiveMarking.CanonicalizeCountry</c>).</para>
///
/// <para><paramref name="Selectors"/> is the full replacement selector set (§21.15) —
/// empty clears it. Each value is validated against this instance's configured catalog,
/// and a category may appear once: two values for one category is a
/// <c>ValidationError</c>. Removing or swapping a selector is a downgrade, exactly like
/// lowering the level.</para>
///
/// <para><paramref name="UkPrefix"/> is the national qualifier as a toggle (§21.12):
/// true renders <c>UK</c> before the level, false renders the bare level. It grants and
/// denies nothing, so it is validated against nothing, and toggling it is never a
/// downgrade.</para>
/// </summary>
public sealed record SetPageMarkingRequest(
    Guid PageId,
    ClassificationLevel Level,
    IReadOnlyList<string> EyesOnly,
    IReadOnlyList<SelectorValue> Selectors,
    bool UkPrefix);

/// <summary>
/// One page's marking, flattened for callers, with the display strings built server-side
/// so every surface — the SPA, an MCP client, an audit reviewer reading the details —
/// renders identical text. Two renderings of one marking that disagree is a compliance
/// problem, so <c>Label</c> has exactly one implementation
/// (<c>ProtectiveMarking.Format</c>) and it is this one.
///
/// <para><b>Two display strings, and choosing wrongly matters.</b> <c>Label</c> is the
/// WHOLE marking — prefix, level, selectors, caveat (<c>UK SECRET APPLE UK EYES ONLY</c>)
/// — and is what anything claiming to show "this page's marking" must render.
/// <c>LevelName</c> is the level ALONE in its UK written form (<c>OFFICIAL-SENSITIVE</c>,
/// <c>TOP SECRET</c>), for the surfaces that have nowhere to put a full marking: a
/// one-word list badge, a radio option in a picker. It exists because otherwise a client
/// would transliterate the GraphQL enum itself — <c>OFFICIAL_SENSITIVE</c> is a machine
/// identifier, not a marking — and §21.1's "one method per spelling, on the server, so
/// they cannot drift" would be quietly broken by the one consumer that matters.</para>
///
/// <para>Rendering <c>LevelName</c> where <c>Label</c> belongs understates the marking:
/// it drops the selectors and the caveat, so a page released only to UK nationals would
/// read as plain SECRET. That direction is the safer error (the caveat is still
/// *enforced* regardless — the badge is informational, not the control), but it is still
/// wrong, and it is why the fields are named to be hard to confuse rather than
/// <c>label</c>/<c>levelLabel</c>. <c>LevelName</c> maps 1:1 onto
/// <c>ProtectiveMarking.LevelName</c>, so its provenance is one grep away.</para>
///
/// <para><c>UkPrefix</c> is the prefix <i>toggle</i> (design.md §21.12): true when the
/// marking carries the UK prefix, false when it carries none. A legacy prefix that is
/// neither (a pre-toggle row a bundle brought across) still renders in <c>Label</c>
/// verbatim, so what the badge shows is always what is stored; only the toggle reads
/// false for it.</para>
///
/// <para>Deliberately not the <c>PageMarking</c> entity: that carries a <c>Page</c>
/// navigation, and returning it from a read path would open a Page-shaped route around
/// the object-level authorization every such field goes through — the same reasoning
/// that produced <c>LabelRef</c> and <c>PagePropertyValue</c>.</para>
/// </summary>
public sealed record PageMarkingView(
    ClassificationLevel Level,
    IReadOnlyList<string> EyesOnly,
    IReadOnlyList<SelectorValue> Selectors,
    bool UkPrefix,
    string Label,
    string LevelName)
{
    /// <summary>Builds the view; the catalog only decides the selectors' display order
    /// in <c>Label</c> (see <c>ProtectiveMarking.Format</c>).</summary>
    public static PageMarkingView From(ProtectiveMarking marking, SelectorCatalog catalog) =>
        new(
            marking.Level,
            marking.EyesOnly,
            marking.Selectors,
            string.Equals(marking.Prefix, ProtectiveMarking.UkPrefix, StringComparison.Ordinal),
            marking.Format(catalog),
            ProtectiveMarking.LevelName(marking.Level));

    // No instance methods here, deliberately: this record IS a GraphQL object type, and
    // Hot Chocolate infers a field from every public instance member. An instance helper
    // would silently grow the published schema (and, for anything returning
    // ProtectiveMarking, drag a second representation of a marking into the SDL beside
    // this one). The inverse of From lives as an extension method in the API layer, where
    // it is needed - see RocketWiki.Api.Markings.PageMarkingViewExtensions.
}
