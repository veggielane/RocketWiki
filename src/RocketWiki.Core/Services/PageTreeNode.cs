using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

/// <summary>
/// One entry of a space's page tree (design.md §8's <c>tree</c>, §6.7/§21.9): either a
/// page the caller may view (<see cref="PageTreeNode"/>, with its children) or a page they
/// may not (<see cref="ProtectedTreeNode"/>, a leaf carrying its position and its denial
/// and nothing else). A closed hierarchy: the walk produces exactly these two, and a
/// consumer that wants "the visible tree" takes <c>OfType&lt;PageTreeNode&gt;()</c> at each
/// level — which is what every omitting surface (MCP, search, the GraphQL tree until it
/// learns to render placeholders) does.
///
/// <para>A space the caller cannot enter at all never yields entries: the whole tree is
/// <c>Denied("no-space-access")</c> (see <c>IPageReadService.GetPageTreeAsync</c>), so a
/// protected entry always sits inside a space whose S gate the caller passed, and its
/// denial may carry the marking (§21.8).</para>
/// </summary>
public abstract record PageTreeEntry
{
    private protected PageTreeEntry()
    {
    }
}

/// <summary>
/// design.md §8's `tree: [PageTreeNode!]!` field. A node the caller CAN view: a node that
/// fails canView never becomes one of these — it becomes a <see cref="ProtectedTreeNode"/>
/// at the same position (design.md §6.7), so there is no way to construct a PageTreeNode
/// for a page the caller can't see, by construction.
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
/// a PageTreeNode because the caller PASSED every one of these rules, so each
/// expression shown is part of "why I can see this page" — the same effective rules
/// §6.6's banner lists to viewers of a restricted page. This is what lets the move
/// dialog warn that a move changes who can see a page (§6.4) without a second query
/// per node.</item>
/// <item><see cref="Marking"/> — this page's protective marking (design.md §21).
/// Leak-safe by exactly the same construction as OwnViewRestrictions: this node is
/// only a PageTreeNode because the caller passed every marking gate for it, so showing
/// it is showing them why they were let in, not a second secret. It is the <b>same
/// value the walk gated on</b>, carried out rather than re-loaded — a second lookup
/// could in principle read a different row than the one the gate consulted, and a tree
/// that displayed a marking other than the one it enforced would be exactly the wrong
/// kind of wrong.</item>
/// <item><see cref="Children"/> — every child at this level, visible or protected, in
/// tree order (SortOrder, then Id). A protected child is a leaf; its own subtree is never
/// walked (restrictions accumulate, so nothing beneath it could be shown anyway, and a
/// tree cannot render a node whose parent is absent).</item>
/// </list>
/// </summary>
public sealed record PageTreeNode(
    Guid Id,
    string Title,
    /// <summary>Decoration only (design.md §4) — the tree shows it beside the title; nothing gates on it.</summary>
    PageIcon? Icon,
    string Slug,
    int SortOrder,
    bool HasRestrictions,
    IReadOnlyList<PageTreeRestriction> OwnViewRestrictions,
    PageMarkingView Marking,
    IReadOnlyList<PageTreeEntry> Children) : PageTreeEntry;

/// <summary>
/// A page the caller may not view, at its position in the tree (design.md §6.7/§21.8):
/// the placeholder's whole content is its <see cref="Denial"/> — the marking, and every
/// gate the caller failed. <b>Deliberately no id, title, slug, icon or children</b>: the
/// "why" needs none of them, the restricted-placeholders leak analysis holds only because
/// none of them travel, and a consumer that wants to omit rather than disclose simply
/// filters the type away. <see cref="SortOrder"/> is kept so the placeholder sits where
/// the page sits, which is the one thing a placeholder is for.
/// </summary>
public sealed record ProtectedTreeNode(int SortOrder, PageDenial Denial) : PageTreeEntry;

/// <summary>One view restriction attached directly to a tree node — id (for chain
/// diffing in the move dialog) plus the expression the caller satisfied. Never an
/// edit rule and never an unpassed rule; see <see cref="PageTreeNode"/>'s doc.</summary>
public sealed record PageTreeRestriction(Guid RuleId, string ExpressionJson);
