using RocketWiki.Core.Access;

namespace RocketWiki.Core.Services;

/// <summary>
/// The document graph (design.md §6.7's omitting surfaces): pages as nodes and the
/// <c>page://</c> links in their live content as directed edges, read from the
/// <see cref="Entities.PageLink"/> index rather than by re-scanning content.
///
/// <para><b>This is an OMITTING surface, like search, Ask and every MCP tool — never a
/// disclosing one.</b> A page the principal cannot view is absent: no placeholder node,
/// no <c>(protected)</c> entry, no edge to or from it, and no count that includes it. The
/// tree and <c>Page.linkTargets</c> disclose denials as placeholders because the reader
/// is inside a space and needs a name for the gap; a graph of the whole instance is a
/// compilation, and a placeholder in a compilation is a census of what the caller may not
/// read (§21.8). An edge with one hidden endpoint would be that census with the name left
/// off, so an edge exists only when the caller can view BOTH of its endpoints.</para>
///
/// <para>Every verdict comes from the batched permission path
/// (<c>PermissionContextLoader</c> + <c>EffectivePermissionCalculator</c>), the same gate
/// walk every other read inherits (§21.9); nothing here evaluates a rule of its own.
/// Trashed pages and pages in archived spaces are never nodes, so a link to or from one
/// is never an edge. No denial is audited here — a pruned compilation is not a refused
/// request (the tree's contract says the same) — and the API layer records the read
/// itself as one audited operation under its own action.</para>
/// </summary>
public interface IPageGraphService
{
    /// <summary>
    /// Every page the principal can view, and every link between two such pages —
    /// instance-wide, or the subgraph induced on one space when <paramref name="spaceId"/>
    /// is given (nodes in that space, edges among them; a link that leaves the space has
    /// no node to end on in the filtered view and is not an edge). A space that does not
    /// exist, is archived, or admits the caller to nothing yields an empty graph, and the
    /// three are indistinguishable by design (§6.7). Nodes are ordered by space key, then
    /// title, then id; edges by source, then ordinal.
    /// </summary>
    Task<PageGraph> GetGraphAsync(Guid? spaceId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page's neighbours: the targets its content links to, in first-occurrence
    /// order, and the pages whose content links to it — each list holding only pages the
    /// principal can view, so a count is the length of its list and never one more.
    /// Found/NotFound/Denied under exactly the conditions <c>IPageReadService.GetPageAsync</c>
    /// applies to the same page id: a caller who cannot view the page learns nothing about
    /// what links to it, and the Denied case exists so the API can audit the refusal.
    /// A missing or trashed target, and a link from a trashed or hidden page, are simply
    /// not in the lists.
    /// </summary>
    Task<ReadResult<PageLinkNeighbours>> GetPageLinksAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);
}
