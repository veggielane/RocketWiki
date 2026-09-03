using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.4 / §21.2: the gates <c>canView</c> and <c>canEdit</c> are the conjunction
/// of, in the order they are <i>reported</i>. View: space access (S), classification (C),
/// selector eligibility (E), selector grant (G), national caveat (N), view restrictions
/// (R). Edit, on top of view: the replica invariant, the space role, edit restrictions.
/// The order is about which reason a denial names — every gate is a conjunct, so no
/// ordering can change a verdict.
/// </summary>
public enum GateKind : byte
{
    /// <summary>S — some access grant in the space matches the principal (§6.4).</summary>
    SpaceAccess = 1,

    /// <summary>C — clearance is at or above the marking's level (§21.3).</summary>
    Classification = 2,

    /// <summary>E — the principal is eligible for a selector's category (§21.15).</summary>
    SelectorEligibility = 3,

    /// <summary>G — a matching access grant confers the selector value (§21.15).</summary>
    SelectorGrant = 4,

    /// <summary>N — the caveat is empty or the principal holds a nationality in it (§21.4).</summary>
    NationalCaveat = 5,

    /// <summary>R — one view restriction on the page or an ancestor (§6.4).</summary>
    ViewRestriction = 6,

    /// <summary>The replica invariant: canEdit is unconditionally false on a replica (§12).</summary>
    ReplicaReadOnly = 7,

    /// <summary>A role grant of Editor or above (§6.4). Confers no visibility.</summary>
    SpaceRole = 8,

    /// <summary>One edit restriction on the page or an ancestor (§6.4).</summary>
    EditRestriction = 9,
}

/// <summary>
/// One evaluated gate, in the structured form the §6.6 inspector and §6.7's disclosed
/// denial both read. <see cref="Reason"/> is the same bounded token the enforcement gate
/// puts in an audit row (<c>no-space-access</c>, <c>classification:secret</c>,
/// <c>selector:not_eligible:FRUIT</c>, <c>caveat:eyes_only</c>,
/// <c>restriction:{pageId}:{ruleId}</c>, …) — null when the gate passed — so the
/// explanation and the audit log can never disagree about a denial's spelling.
///
/// <para><b>What travels where.</b> A reason token carries at most a category NAME
/// (bounded configured vocabulary, like a level); the selector VALUE, the rule id, the
/// chain page id, the action and the expression travel only in the typed members here, so
/// a consumer that renders reasons renders nothing it did not choose to. The API projects
/// this once into its wire shape and decides what a denied caller may see (§21.8); this
/// type is the complete internal record, not the disclosure policy.</para>
/// </summary>
/// <param name="Kind">Which gate.</param>
/// <param name="Passed">Whether the principal passed it.</param>
/// <param name="Reason">The audit-row token when failed; null when passed.</param>
/// <param name="SelectorCategory">E/G: the category name.</param>
/// <param name="SelectorValue">E/G: the value within the category.</param>
/// <param name="RuleId">R: the restriction rule.</param>
/// <param name="PageId">R: the chain page the rule is attached to (not necessarily the inspected page).</param>
/// <param name="Action">R: view or edit.</param>
/// <param name="ExpressionJson">R: the rule's expression.</param>
public sealed record GateCheck(
    GateKind Kind,
    bool Passed,
    string? Reason,
    string? SelectorCategory = null,
    string? SelectorValue = null,
    Guid? RuleId = null,
    Guid? PageId = null,
    PageAction? Action = null,
    string? ExpressionJson = null)
{
    /// <summary>A passed gate of the given kind, with no detail.</summary>
    public static GateCheck Pass(GateKind kind) => new(kind, true, null);

    /// <summary>A failed gate of the given kind carrying its audit-row reason.</summary>
    public static GateCheck Fail(GateKind kind, string reason) => new(kind, false, reason);

    /// <summary>A gate check from an enforcement result: passed, or failed with its reason.</summary>
    public static GateCheck From(GateKind kind, PermissionCheckResult result) =>
        result.IsAllowed ? Pass(kind) : Fail(kind, result.DenialReason!);

    /// <summary>The enforcement-shaped view of this check — what <c>Compute</c> would have
    /// returned for this gate alone.</summary>
    public PermissionCheckResult ToResult() =>
        Passed ? PermissionCheckResult.Allow() : PermissionCheckResult.Deny(Reason!);

    /// <summary>The enforcement-shaped view of a gate list: the first failed entry's
    /// reason, or allow when every entry passed. THE collapse every "Check" wrapper uses
    /// over its "Evaluate" form, so a first-failure verdict is spelled once.</summary>
    public static PermissionCheckResult ResultOf(IEnumerable<GateCheck> checks)
    {
        foreach (var check in checks)
        {
            if (!check.Passed)
            {
                return check.ToResult();
            }
        }

        return PermissionCheckResult.Allow();
    }
}
