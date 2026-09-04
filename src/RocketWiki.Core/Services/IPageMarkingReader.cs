using RocketWiki.Core.Access;

namespace RocketWiki.Core.Services;

/// <summary>
/// Batch read of protective markings <b>for display</b> (design.md §21.13): the markings
/// of pages the caller has <i>already</i> been permitted to see, so a badge, a citation,
/// an MCP payload or an aggregate label can name what the text in front of the reader is.
///
/// <para><b>This is not an enforcement seam and must never become one.</b> Enforcement
/// reads markings through <c>PermissionContextLoader</c>, inside
/// <c>EffectivePermissionCalculator.Compute</c>, on the same call that decides canView —
/// there is no path by which a caller reaches a decision through this interface, because
/// this interface returns no decision. Every id handed to it belongs to a page that
/// already passed the marking gate, which is exactly why showing the marking discloses
/// nothing: it is the reason the caller was let in, not a second secret (the
/// <c>Page.marking</c> / <c>PageTreeNode.Marking</c> posture, §21.9).</para>
///
/// <para><b>It does not filter.</b> Handing it the id of a page the caller cannot view
/// would answer with that page's marking, because it has no principal and makes no
/// judgement. Callers must therefore only ever pass ids that survived a real read path —
/// search hits, retrieved pages, pruned tree nodes. That is a caller's obligation, stated
/// here so nobody discovers it by accident.</para>
///
/// <para>One query per batch, countries included: the display side sits on the same hot
/// paths the control does (a search page, a whole space's tree), and an N+1 there is how
/// a marking badge gets quietly turned off.</para>
/// </summary>
public interface IPageMarkingReader
{
    /// <summary>
    /// Every requested id is present in the result. A page with no marking row resolves
    /// to <see cref="ProtectiveMarking.FailClosed"/> — TOP SECRET, no prefix — the same
    /// substitution every other read path makes (§21.5), so no consumer ever holds a
    /// nullable marking it could decide to render as blank. A blank badge is the one
    /// answer that misrepresents the enforcement the caller is actually subject to.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ProtectiveMarking>> LoadAsync(
        IReadOnlyCollection<Guid> pageIds, CancellationToken cancellationToken = default);
}
