namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §8's `tree: [PageTreeNode!]!` field. A pruned view, not the full Page
/// entity: a node that fails canView is dropped along with its entire Children subtree
/// before it ever gets here (design.md §6.7) - there is no way to construct a
/// PageTreeNode for a page the caller can't see, by construction.
///
/// Restriction data on a tree node (design.md §6.4/§6.6), leak posture stated once:
/// <list type="bullet">
/// <item><see cref="HasRestrictions"/> — true when any restriction (view OR edit) is
/// attached directly to this page; ancestors' rules are not folded in, because the
/// tree shape already makes inheritance visible and accumulation is the consumer's
/// concern (the move dialog accumulates chains itself). Existence of a restriction on
/// a page you can view is deliberately not a secret: §6.6's restriction banner shows
/// a lock badge to exactly this audience. Edit-restriction *contents* are NOT exposed
/// here — only this boolean.</item>
/// <item><see cref="OwnViewRestrictions"/> — the view restrictions attached directly
/// to this node, with their expressions. Leak-safe by construction: this node is only
/// present in the pruned tree because the caller PASSED every one of these rules, so
/// each expression shown is part of "why I can see this page" — the same effective
/// rules §6.6's banner lists to viewers of a restricted page. This is what lets the
/// move dialog warn that a move changes who can see a page (§6.4) without a second
/// query per node.</item>
/// <item><see cref="Marking"/> — this page's protective marking (design.md §21).
/// Leak-safe by exactly the same construction as OwnViewRestrictions: this node is
/// only present because the caller passed the clearance gate for this marking, so
/// showing it is showing them why they were let in, not a second secret. It is the
/// <b>same value the walk gated on</b>, carried out rather than re-loaded — a second
/// lookup could in principle read a different row than the one pruning consulted, and
/// a tree that displayed a marking other than the one it enforced would be exactly
/// the wrong kind of wrong.</item>
/// </list>
/// </summary>
public sealed record PageTreeNode(
    Guid Id,
    string Title,
    string Slug,
    int SortOrder,
    bool HasRestrictions,
    IReadOnlyList<PageTreeRestriction> OwnViewRestrictions,
    PageMarkingView Marking,
    IReadOnlyList<PageTreeNode> Children);

/// <summary>One view restriction attached directly to a tree node — id (for chain
/// diffing in the move dialog) plus the expression the caller satisfied. Never an
/// edit rule and never an unpassed rule; see <see cref="PageTreeNode"/>'s doc.</summary>
public sealed record PageTreeRestriction(Guid RuleId, string ExpressionJson);
