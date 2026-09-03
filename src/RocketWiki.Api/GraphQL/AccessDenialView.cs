using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The gates a page read is decided by, as the wire names them (design.md §6.4/§21.2):
/// the view ladder S, C, E, G, N, R, then the two edit-only gates the §6.6 inspector
/// also reports. <see cref="Replica"/> and <see cref="Role"/> never appear in a denial's
/// reasons — a view denial evaluates no edit gate — and exist here so the inspector's
/// <c>editGates</c> share one vocabulary with the placeholder's <c>reasons</c>.
/// </summary>
public enum AccessGate
{
    SpaceAccess,
    Classification,
    SelectorEligibility,
    SelectorGrant,
    NationalCaveat,
    Restriction,
    Replica,
    Role,
}

/// <summary>
/// One evaluated gate on the wire — the placeholder's <c>reasons</c> (design.md §6.7,
/// §21.8) and the inspector's <c>viewGates</c>/<c>editGates</c> (§6.6) both list these,
/// so there is one gate vocabulary and one leak analysis. Flat, nullable detail fields
/// in the <c>PageMutationErrorView</c> convention: only the fields relevant to
/// <see cref="Gate"/> are non-null.
///
/// <para><b>This is the one projection from Core's <see cref="GateCheck"/>, and it is
/// where the disclosure policy lives.</b> <see cref="GateCheck"/> is the complete internal
/// record — it carries the chain page id, the rule's action and its expression — and
/// none of those travel: the page id is a UUIDv7 (a timestamp) naming a page the caller
/// cannot view, and the expression says who <i>can</i> see the page (§6.4.1/§6.6's title
/// rule, extended to expressions). What does travel is already the caller's to know:
/// the level and countries are the marking they are shown beside, the category and
/// value are in its label, and the rule id is the token the audit row carries
/// (<c>restriction:{pageId}:{ruleId}</c>) and the inspector already discloses in self
/// mode. Pinned by an introspection allowlist test.</para>
/// </summary>
/// <param name="Gate">Which gate.</param>
/// <param name="Passed">Whether the subject passed it. Always false inside a denial's
/// <c>reasons</c> (Core lists only failed gates there); meaningful in the inspector,
/// where every gate is listed.</param>
/// <param name="RequiredLevel">CLASSIFICATION: the page's level, when the marking is in hand.</param>
/// <param name="RequiredLevelName">CLASSIFICATION: the level's UK written form.</param>
/// <param name="Category">SELECTOR_ELIGIBILITY / SELECTOR_GRANT: the selector's category.</param>
/// <param name="Value">SELECTOR_ELIGIBILITY / SELECTOR_GRANT: the selector's value.</param>
/// <param name="Countries">NATIONAL_CAVEAT: the caveat's country set, when the marking is in hand.</param>
/// <param name="RuleId">RESTRICTION: the rule. Never the page it sits on, never its expression.</param>
/// <param name="Inherited">RESTRICTION: whether the rule sits on an ancestor rather than
/// the subject page. False on a tree placeholder, by construction: an ancestor whose
/// rule the caller fails is itself the placeholder, and nothing beneath it is walked.</param>
/// <param name="RequiredRole">ROLE: the minimum role the action needs.</param>
[GraphQLName("GateResult")]
public sealed record GateResultView(
    AccessGate Gate,
    bool Passed,
    ClassificationLevel? RequiredLevel,
    string? RequiredLevelName,
    string? Category,
    string? Value,
    IReadOnlyList<string>? Countries,
    Guid? RuleId,
    bool? Inherited,
    SpaceRole? RequiredRole)
{
    /// <summary>
    /// THE projection. <paramref name="marking"/> is the page's marking when the caller
    /// may be shown it (a denial after space access, never before — see
    /// <see cref="AccessDenialView.From"/>) and null otherwise; it feeds only the level
    /// and the country set. <paramref name="subjectPageId"/> is the page the gates were
    /// evaluated for, when the caller holds it; null means a tree placeholder, whose
    /// failing restrictions are its own by construction (see <see cref="Inherited"/>).
    /// </summary>
    internal static GateResultView From(GateCheck check, ProtectiveMarking? marking, Guid? subjectPageId)
    {
        var gate = check.Kind switch
        {
            GateKind.SpaceAccess => AccessGate.SpaceAccess,
            GateKind.Classification => AccessGate.Classification,
            GateKind.SelectorEligibility => AccessGate.SelectorEligibility,
            GateKind.SelectorGrant => AccessGate.SelectorGrant,
            GateKind.NationalCaveat => AccessGate.NationalCaveat,
            GateKind.ViewRestriction => AccessGate.Restriction,
            GateKind.EditRestriction => AccessGate.Restriction,
            GateKind.ReplicaReadOnly => AccessGate.Replica,
            GateKind.SpaceRole => AccessGate.Role,
            // A gate kind this projection does not know must not be rendered as one it
            // does: fail loudly at the boundary rather than mislabel a reason.
            _ => throw new InvalidOperationException($"No wire gate for {check.Kind}."),
        };

        var isRestriction = gate == AccessGate.Restriction;
        return new GateResultView(
            gate,
            check.Passed,
            RequiredLevel: gate == AccessGate.Classification ? marking?.Level : null,
            RequiredLevelName: gate == AccessGate.Classification && marking is not null
                ? ProtectiveMarking.LevelName(marking.Level)
                : null,
            Category: gate is AccessGate.SelectorEligibility or AccessGate.SelectorGrant ? check.SelectorCategory : null,
            Value: gate is AccessGate.SelectorEligibility or AccessGate.SelectorGrant ? check.SelectorValue : null,
            Countries: gate == AccessGate.NationalCaveat ? marking?.EyesOnly : null,
            RuleId: isRestriction ? check.RuleId : null,
            Inherited: isRestriction
                ? subjectPageId is { } subject && check.PageId is { } chainPage && chainPage != subject
                : null,
            RequiredRole: gate == AccessGate.Role ? SpaceRole.Editor : null);
    }
}

/// <summary>
/// What a caller is told about a page they cannot view (design.md §6.7 / §21.8): the
/// <c>(protected)</c> placeholder, the page's marking, and every gate they failed — or,
/// when no access grant admits them to the space at all, only that fact. Rendered by
/// <c>pageAccess</c>, <c>pageAccessBySlug</c>, the tree's <c>ProtectedTreeNode</c>,
/// <c>Page.linkTargets</c> and <c>Page.parentDenial</c>, and by nothing else: search,
/// Ask, MCP and every listing keep omitting (§21.8).
///
/// <para><b>Nothing here identifies the page.</b> No id, title, slug, timestamp, author,
/// child count or content, and no rule expression or ancestor title inside
/// <see cref="Reasons"/> — the placeholder's whole content is the marking and the
/// gates. An introspection test pins the field list of this type, of
/// <see cref="GateResultView"/> and of the tree placeholder to exactly that.</para>
///
/// <para><b>The placeholder text is a server constant</b> (<see cref="ProtectedTitle"/>),
/// the only place the string exists: the tree placeholder's <c>title</c> and this
/// <c>placeholderTitle</c> are the same value, so a client renders it verbatim and
/// never invents a second spelling.</para>
/// </summary>
/// <param name="NoSpaceAccess">True when the caller matches no ACCESS grant in the page's
/// space (roles never supersede access, §6.4). <see cref="Marking"/> is then null and
/// <see cref="Reasons"/> holds the single SPACE_ACCESS entry.</param>
/// <param name="Marking">The page's own marking — label, level, selectors, caveat —
/// withheld when <see cref="NoSpaceAccess"/>.</param>
/// <param name="Reasons">Every view gate the caller failed, in ladder order (S, C, E, G,
/// N, R).</param>
[GraphQLName("AccessDenial")]
public sealed record AccessDenialView(
    bool NoSpaceAccess,
    PageMarkingView? Marking,
    IReadOnlyList<GateResultView> Reasons)
{
    /// <summary>The placeholder a denied page renders as, everywhere it is disclosed.</summary>
    public const string ProtectedTitle = "(protected)";

    public string PlaceholderTitle => ProtectedTitle;

    /// <summary>
    /// Projects Core's denial (<see cref="PageDenial"/>) to the wire. Core already applies
    /// the withholding rule — a denial with <see cref="PageDenial.NoSpaceAccess"/> carries
    /// no marking and only the S gate — and this projection applies it again rather than
    /// trusting that: the C/E/G/N tokens embed the level and the category names, and a
    /// caller who cannot enter the space is told nothing about the marking (§21.8).
    /// </summary>
    /// <param name="subjectPageId">The page the denial is for, when the caller holds its
    /// id (a root field, a link target, a parent); null for a tree placeholder, which has
    /// none and whose failing restrictions are its own — see
    /// <see cref="GateResultView.Inherited"/>.</param>
    public static AccessDenialView From(PageDenial denial, SelectorCatalog catalog, Guid? subjectPageId)
    {
        if (denial.NoSpaceAccess)
        {
            return new AccessDenialView(
                NoSpaceAccess: true,
                Marking: null,
                denial.Reasons
                    .Where(g => g.Kind == GateKind.SpaceAccess)
                    .Select(g => GateResultView.From(g, marking: null, subjectPageId))
                    .ToList());
        }

        var marking = denial.Marking;
        return new AccessDenialView(
            NoSpaceAccess: false,
            marking is null ? null : PageMarkingView.From(marking, catalog),
            denial.Reasons.Select(g => GateResultView.From(g, marking, subjectPageId)).ToList());
    }
}
