using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.7: "every read path enforces canView" - page fetch, tree (filtered
/// during the walk, ancestor restrictions carried down the recursion), and revision
/// history. This is the shared read path GraphQL resolvers and MCP tools both call
/// (design.md §8); search and attachment streaming enforce canView through their own
/// paths and are not part of this interface.
///
/// The load-bearing rule for every method here: a page that fails canView must be
/// indistinguishable from a page that does not exist *to the caller of the API* - but
/// per design.md §6.7 ("indistinguishable to the caller, not to the audit log") the
/// distinction must survive internally so §7 can record the denial with its failing
/// restriction. Hence <see cref="ReadResult{T}"/>: NotFound and Denied are separate
/// cases here, the API layer audits Denied, and both collapse to the identical
/// null/empty response at the boundary. A service that returned bare null for both
/// would make denial auditing impossible - §6.7 names that as the anti-pattern.
/// </summary>
public interface IPageReadService
{
    /// <summary>
    /// Found(page) if the principal can view it; NotFound if no such live page exists;
    /// Denied(reason) if it exists but canView fails - where reason is the failing
    /// gate's token (<c>no-space-access</c>, a <c>classification:</c>/<c>selector:</c>/
    /// <c>caveat:</c> token, or <c>restriction:{pageId}:{ruleId}</c>), exactly as
    /// EffectivePermissionCalculator computed it.
    /// </summary>
    Task<ReadResult<Page>> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The DISCLOSED form of the same read (design.md §6.7 / §21.8): NotFound if no such
    /// live page exists; Found(page) if the principal can view it; otherwise
    /// Denied(<see cref="PageDenial"/>) — the page's marking and every gate the principal
    /// failed, or, when no access grant admits them to the space at all, only that fact.
    /// The verdict is exactly <see cref="GetPageAsync"/>'s (one ladder decides both); what
    /// differs is that this result is meant to reach the caller as a placeholder, where
    /// <see cref="GetPageAsync"/>'s Denied exists for the audit row and collapses to null.
    /// A caller that omits keeps using <see cref="GetPageAsync"/>; a caller that discloses
    /// uses this, and the API still audits the denial exactly once either way.
    /// </summary>
    Task<PageAccess> GetPageAccessAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="GetPageAccessAsync"/> for many pages at once — a constant number of
    /// queries for the whole batch, for the surfaces that resolve several page references
    /// in one request (the link targets inside a page's content). Every requested id has
    /// an entry: NotFound for an id that names no live page, so a missing page and a
    /// denied one are distinct here and the caller decides what each becomes on the wire.
    /// No per-target audit row: the page whose content named the targets was the read that
    /// was audited.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PageAccess>> GetPageAccessBatchAsync(
        IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The id of the live page addressed by <c>/spaces/{spaceKey}/{slug}</c>, or null
    /// if no such page exists. Slugs are unique per space, so this is unambiguous.
    ///
    /// <para>Deliberately returns an ID and makes NO access decision: the caller feeds
    /// it straight to <see cref="GetPageAsync"/>, which is the one place canView and
    /// the clearance gate live. Resolving the slug and authorizing it in one method
    /// would be a second enforcement path to keep in step with the first — and the
    /// caller collapsing "no such slug" and "denied" to the same null is what keeps a
    /// slug URL from becoming an existence oracle (§6.7).</para>
    /// </summary>
    Task<Guid?> FindPageIdBySlugAsync(string spaceKey, string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// The space's page tree, decided node by node with the calculator's own view ladder
    /// (design.md §6.7/§21.9). A node the principal can view is a <see cref="PageTreeNode"/>
    /// with its children; a node they cannot is a <see cref="ProtectedTreeNode"/> leaf at
    /// the same position, carrying the page's marking and every failed gate and nothing
    /// else - its subtree is never walked (restrictions only ever accumulate going down,
    /// so a hidden ancestor implies every descendant is hidden too - §6.4). A protected
    /// entry is not a Denied result: the browse itself was permitted and shows the
    /// placeholder (or, for a surface that omits, shows less - the same as it does for
    /// everyone), so per-node denials are not reported or audited here.
    /// Denied("no-space-access") is returned only when the request as a whole is refused
    /// because no access grant in the space admits the principal - roles never supersede
    /// access, so a Space-admin without one is refused like a stranger; NotFound when the
    /// space doesn't exist. The API boundary collapses both to the same empty list Found
    /// can also legitimately carry, which is what keeps a space the caller cannot enter
    /// indistinguishable from one that does not exist.
    /// </summary>
    Task<ReadResult<IReadOnlyList<PageTreeEntry>>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>Found/NotFound/Denied under the exact same conditions as GetPageAsync for the same pageId - revision history requires nothing beyond canView on the page itself.</summary>
    Task<ReadResult<IReadOnlyList<PageRevision>>> GetRevisionHistoryAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Many pages at once, each gated by the same <c>canView</c> as
    /// <see cref="GetPageAsync"/> — a constant number of queries for the whole batch, not
    /// one call per id.
    ///
    /// <para>A page that does not exist, or that the principal cannot view, is simply
    /// <b>absent from the result</b> — the two are indistinguishable to the caller, per
    /// §6.7. That is why this returns a dictionary rather than a <see cref="ReadResult{T}"/>
    /// per key: its only caller is the batching layer behind list-resolved fields, where a
    /// missing key already reads as "no value", and the denial for a directly-requested
    /// page is audited on the single-page path that requested it.</para>
    ///
    /// <para>This exists because the alternative — looping <see cref="GetPageAsync"/> over
    /// the batch — is either N×3 round trips serially, or, if parallelized, N concurrent
    /// operations on one <c>DbContext</c>, which EF refuses outright ("a second operation
    /// was started on this context instance"). That failure took out every page view in
    /// the SPA once already; see DataLoaderDbContext.</para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Page>> GetPagesAsync(
        IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The ids of each given page's directly-visible children, in tree order (SortOrder,
    /// then Id), for MANY parents at once — a constant number of queries for the whole
    /// batch, so a list of pages resolving <c>children</c> costs one round of work rather
    /// than one per page.
    ///
    /// <para><b>Each child is gated on its own</b>, against its own ancestor restriction
    /// chain and its own marking, rather than by locating the parent inside a pruned
    /// space tree. That is the only reading consistent with §21.5: a marking does not
    /// accumulate, so a page whose ANCESTOR is above the caller's clearance is pruned
    /// from the tree along with its subtree — yet the design says that page "stays
    /// reachable by id and through search, both of which check it on its own". Deriving
    /// children from the tree made such a page answer <c>children</c> with an empty list
    /// even though it and its children were perfectly viewable, so <c>page(id:)</c> and
    /// the tree disagreed about the same subtree.</para>
    ///
    /// <para>A parent id that does not exist, or whose children are all hidden, maps to
    /// an empty list — indistinguishably, per §6.7. No <see cref="ReadResult{T}"/> here
    /// and no denial to audit: a pruned listing is not a refused request (see
    /// <see cref="GetPageTreeAsync"/>'s contract), and every caller of this has already
    /// resolved the parent Page through a gate that audited whatever it decided.</para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetVisibleChildIdsAsync(
        IReadOnlyCollection<Guid> parentPageIds, Principal principal, CancellationToken cancellationToken = default);
}
