using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Data.Access;

/// <summary>
/// The page a permission is being computed for, reduced to the three fields the
/// authorization inputs are keyed by. Carries the materialized <c>AncestorPath</c>
/// rather than a parsed chain so callers that never load a <see cref="Page"/> entity
/// (search candidates projected straight from SQL) reach the chain through the same
/// parse as everyone else.
/// </summary>
internal readonly record struct PermissionSubject(Guid PageId, Guid SpaceId, string AncestorPath)
{
    public static PermissionSubject For(Page page) => new(page.Id, page.SpaceId, page.AncestorPath);

    /// <summary>
    /// The page's restriction chain: root-most ancestor first, the page itself last
    /// (design.md §6.4 — restrictions accumulate down the tree).
    /// </summary>
    public IReadOnlyList<Guid> ChainPageIds()
    {
        var ancestors = Page.ParseAncestorIds(AncestorPath);
        var chain = new Guid[ancestors.Count + 1];
        for (var i = 0; i < ancestors.Count; i++)
        {
            chain[i] = ancestors[i];
        }

        chain[^1] = PageId;
        return chain;
    }
}

/// <summary>
/// Loads the rows <see cref="EffectivePermissionCalculator"/> needs for a page — the
/// space's grants and the page-plus-ancestors restriction chain (design.md §6.4) — and
/// hands them back in the calculator's contractual order. Every canView/canEdit
/// computation in RocketWiki.Data goes through here rather than assembling those rows
/// itself, because two properties are easy to lose one call site at a time:
///
/// <para><b>Order.</b> The chain is always root-most ancestor first, the page's own
/// rules last, <c>CreatedAtUtc</c> then <c>Id</c> within a single page.
/// <see cref="EffectivePermissionCalculator.Compute"/> reports the <i>first failing</i>
/// restriction as the denial reason and <see cref="EffectivePermissionCalculator.Explain"/>
/// requires the same input order (see its doc); that reason is what audit rows carry
/// (design.md §7) and what the <c>effectivePermission</c> inspector renders to explain
/// them (§6.6/§6.7). Database enumeration order would let the two disagree and let one
/// denial report differently between requests. Root-most-first also means the reason
/// names the outermost boundary the caller failed. Do not re-sort, filter, or append to
/// what this returns before passing it to the calculator.</para>
///
/// <para><b>Batching.</b> <see cref="LoadBatchAsync(IReadOnlyCollection{PermissionSubject}, CancellationToken)"/>
/// issues exactly three queries — one for the grants of every space involved, one for the
/// restrictions of every page-or-ancestor involved, and one for the protective markings
/// (joined to their country rows) of every subject page — regardless of batch size; the
/// per-page work afterwards is pure in-memory rule evaluation. List paths (search
/// post-filtering, label listings, subtree checks, notification re-checks, per-child
/// permission facts) use it instead of looping over the per-page load, which costs three
/// queries each. The marking query is the reason
/// <see cref="PermissionContextBatch"/> holds a <i>lookup</i> of countries by page id
/// rather than resolving a page's set on demand: search post-filters hundreds of
/// candidates, and a per-candidate country query would be an N+1 sitting directly on the
/// hot path of the feature it protects.</para>
///
/// <para><b>Markings (design.md §21).</b> Unlike restrictions, a marking does NOT
/// accumulate down the tree: inheritance happens once, when a page is created, and is
/// then a value that page owns and an editor may override in either direction. So the
/// marking loaded here is the subject page's own, never its ancestors'. A page id with
/// no marking row resolves to <see cref="ProtectiveMarking.FailClosed"/> — TOP SECRET —
/// and that substitution happens HERE, in the loader, so no consumer of a
/// <see cref="PagePermissionContext"/> ever holds a nullable marking it could decide to
/// ignore.</para>
///
/// <para>Fail closed (design.md §6.3): a space with no grant rows yields an empty grant
/// list, which <see cref="EffectivePermissionCalculator.ComputeSpaceAccess"/> turns into
/// no access, which denies. A page id with no restriction rows contributes nothing to the
/// chain, which is the correct "unrestricted at that level" reading, never a bypass —
/// the space role still has to hold.</para>
/// </summary>
internal sealed class PermissionContextLoader
{
    private readonly RocketWikiDbContext _db;
    private readonly bool _noTracking;

    /// <param name="noTracking">
    /// Matches the owning service's read semantics: pure read models load
    /// <c>AsNoTracking</c>, services that may save in the same unit of work keep the
    /// tracked default. Rule rows are never mutated through this loader either way.
    /// </param>
    public PermissionContextLoader(RocketWikiDbContext db, bool noTracking = false)
    {
        _db = db;
        _noTracking = noTracking;
    }

    private IQueryable<AccessRule> Rules => _noTracking ? _db.AccessRules.AsNoTracking() : _db.AccessRules;

    private IQueryable<PageMarking> Markings => _noTracking ? _db.PageMarkings.AsNoTracking() : _db.PageMarkings;

    /// <summary>Grants + ordered restriction chain + the page's marking, in three queries.</summary>
    /// <param name="isReplicaSpace">
    /// The space's own <see cref="Space.IsReplicaOf"/> result. Not derived here: a
    /// view-only caller has no local instance id to compare against, and canView is
    /// unaffected by it (design.md §6.4 — the replica invariant only ever suppresses
    /// canEdit), so those callers pass false deliberately.
    /// </param>
    public Task<PagePermissionContext> LoadAsync(Page page, bool isReplicaSpace, CancellationToken cancellationToken) =>
        LoadAsync(page.SpaceId, PermissionSubject.For(page).ChainPageIds(), isReplicaSpace, cancellationToken);

    /// <summary>
    /// Grants + ordered restriction chain + marking for an explicit chain of page ids, in
    /// three queries. For callers whose chain is not one page's own — a page being created
    /// under a parent (it inherits the parent's chain), or a move's destination chain.
    /// <paramref name="chainPageIds"/> must already be root-most first; see the class doc.
    ///
    /// <para>The marking used is the <b>last</b> chain element's, which is exactly right
    /// for every caller: for a page's own permission the chain ends with that page, for a
    /// create-under-parent it ends with the parent whose marking the new page will
    /// inherit, and for a move's destination it ends with the page being moved. An EMPTY
    /// chain — creating a page at the root of a space — has no page to read a marking
    /// from and uses <see cref="ProtectiveMarking.Baseline"/> (OFFICIAL), because that is
    /// precisely the marking the root page being created will receive. Failing closed
    /// there would make root-page creation impossible for anyone below TOP SECRET, which
    /// is a bug, not a control.</para>
    /// </summary>
    public async Task<PagePermissionContext> LoadAsync(
        Guid spaceId, IReadOnlyList<Guid> chainPageIds, bool isReplicaSpace, CancellationToken cancellationToken)
    {
        var grants = await LoadSpaceGrantsAsync(spaceId, cancellationToken);
        var restrictions = await LoadOrderedRestrictionsAsync(chainPageIds, cancellationToken);
        var marking = chainPageIds.Count == 0
            ? ProtectiveMarking.Baseline
            : await LoadMarkingAsync(chainPageIds[^1], cancellationToken);
        return new PagePermissionContext(grants, restrictions, isReplicaSpace, marking, _db.SelectorCatalog);
    }

    /// <summary>
    /// One page's marking, or <see cref="ProtectiveMarking.FailClosed"/> when the row is
    /// missing (design.md §21). One query — the country and selector rows ride along
    /// through the navigation includes, which for a single page is the cheapest correct
    /// shape.
    /// </summary>
    public async Task<ProtectiveMarking> LoadMarkingAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var marking = await Markings
            .Include(m => m.Countries)
            .Include(m => m.Selectors)
            .FirstOrDefaultAsync(m => m.PageId == pageId, cancellationToken);
        return marking?.ToMarking() ?? ProtectiveMarking.FailClosed;
    }

    /// <summary>
    /// The markings of an arbitrary set of pages, countries included, in ONE query —
    /// with <see cref="ProtectiveMarking.FailClosed"/> substituted for any page that has
    /// no row, so a caller can never hold a nullable marking it might decide to ignore.
    ///
    /// <para>For callers that need to answer "may this principal be told about this page"
    /// for pages they are not computing a full permission for — the restriction chain's
    /// ANCESTORS, whose titles the §6.6 inspector renders. A marking does not accumulate
    /// down the tree (§21.5), so passing the child's gate says nothing about the parent's,
    /// and the ancestor has to be checked on its own.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ProtectiveMarking>> LoadMarkingsForAsync(
        IReadOnlyCollection<Guid> pageIds, CancellationToken cancellationToken)
    {
        var byPageId = await LoadMarkingsAsync(pageIds as Guid[] ?? pageIds.ToArray(), cancellationToken);
        return pageIds.ToDictionary(id => id, id => byPageId.GetValueOrDefault(id) ?? ProtectiveMarking.FailClosed);
    }

    /// <summary>Every space-scoped grant for one space — access grants (with their
    /// selector rows, §21.15) and role grants alike, one query: the calculator reads both
    /// kinds from the one list. The include is a join that duplicates a grant row per
    /// selector, which EF de-duplicates; acceptable at grant cardinality.</summary>
    public async Task<IReadOnlyList<AccessRule>> LoadSpaceGrantsAsync(Guid spaceId, CancellationToken cancellationToken) =>
        await Rules
            .Include(r => r.Selectors)
            .Where(r => (r.Kind == AccessRuleKind.RoleGrant || r.Kind == AccessRuleKind.AccessGrant) && r.SpaceId == spaceId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The restriction chain alone, ordered per the class doc — one query. For callers
    /// that already hold the space's grants (or need the rules for display rather than
    /// for a verdict).
    /// </summary>
    public async Task<IReadOnlyList<AccessRule>> LoadOrderedRestrictionsAsync(
        IReadOnlyList<Guid> chainPageIds, CancellationToken cancellationToken)
    {
        var byPageId = await LoadRestrictionsAsync(
            chainPageIds as Guid[] ?? chainPageIds.ToArray(), cancellationToken);
        return PermissionContextBatch.Order(chainPageIds, byPageId);
    }

    /// <summary>Three queries for the whole batch — see the class doc's batching guarantee.</summary>
    public Task<PermissionContextBatch> LoadBatchAsync(
        IReadOnlyCollection<PermissionSubject> subjects, CancellationToken cancellationToken) =>
        LoadBatchAsync(subjects, [], cancellationToken);

    /// <summary>
    /// As <see cref="LoadBatchAsync(IReadOnlyCollection{PermissionSubject}, CancellationToken)"/>,
    /// also loading grants for spaces that carry no page in the batch — for callers that
    /// additionally gate space-scoped rows on "holds any role in that space". Still three
    /// queries.
    /// </summary>
    public async Task<PermissionContextBatch> LoadBatchAsync(
        IReadOnlyCollection<PermissionSubject> subjects,
        IReadOnlyCollection<Guid> additionalSpaceIds,
        CancellationToken cancellationToken)
    {
        var spaceIds = subjects.Select(s => s.SpaceId).Concat(additionalSpaceIds).Distinct().ToArray();
        var chainIds = subjects.SelectMany(s => s.ChainPageIds()).Distinct().ToArray();

        var grants = spaceIds.Length == 0
            ? new List<AccessRule>()
            : await Rules
                .Include(r => r.Selectors)
                .Where(r => (r.Kind == AccessRuleKind.RoleGrant || r.Kind == AccessRuleKind.AccessGrant) && r.SpaceId != null && spaceIds.Contains(r.SpaceId.Value))
                .ToListAsync(cancellationToken);

        return new PermissionContextBatch(
            grants.GroupBy(r => r.SpaceId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<AccessRule>)g.ToList()),
            await LoadRestrictionsAsync(chainIds, cancellationToken),
            await LoadMarkingsAsync(SubjectPageIds(subjects), cancellationToken),
            _db.SelectorCatalog);
    }

    /// <summary>
    /// Restrictions + markings batch — TWO queries — for a single-space listing whose
    /// caller already holds that space's grants because it gated the space role before it
    /// looked at any page (design.md §6.7). Re-querying the grants here would be the one
    /// wasted round trip this loader exists to avoid.
    /// </summary>
    public async Task<PermissionContextBatch> LoadBatchAsync(
        IReadOnlyCollection<PermissionSubject> subjects,
        Guid spaceId,
        IReadOnlyList<AccessRule> spaceGrants,
        CancellationToken cancellationToken)
    {
        var chainIds = subjects.SelectMany(s => s.ChainPageIds()).Distinct().ToArray();
        return new PermissionContextBatch(
            new Dictionary<Guid, IReadOnlyList<AccessRule>> { [spaceId] = spaceGrants },
            await LoadRestrictionsAsync(chainIds, cancellationToken),
            await LoadMarkingsAsync(SubjectPageIds(subjects), cancellationToken),
            _db.SelectorCatalog);
    }

    /// <summary>The pages a marking is needed for: the subjects themselves, never their
    /// ancestors. A marking is a page's own property — inheritance happens once at
    /// creation and is not re-derived at read time (design.md §21).</summary>
    private static Guid[] SubjectPageIds(IReadOnlyCollection<PermissionSubject> subjects) =>
        subjects.Select(s => s.PageId).Distinct().ToArray();

    /// <summary>Every restriction attached to any of these page ids, grouped by page —
    /// one query, or none at all for an empty id set.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<AccessRule>>> LoadRestrictionsAsync(
        Guid[] pageIds, CancellationToken cancellationToken)
    {
        if (pageIds.Length == 0)
        {
            return [];
        }

        var restrictions = await Rules
            .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && pageIds.Contains(r.PageId.Value))
            .ToListAsync(cancellationToken);
        return restrictions
            .GroupBy(r => r.PageId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AccessRule>)g.ToList());
    }

    /// <summary>
    /// Every marking for these page ids, with its country set, in <b>ONE</b> query
    /// regardless of batch size — the countries ride along on the collection include
    /// rather than being fetched per page, which on the search post-filter path (hundreds
    /// of candidates) is the difference between a constant round trip and an N+1 sitting
    /// on the hot path of the control itself.
    ///
    /// <para>Pages absent from the result are simply absent from the dictionary;
    /// <see cref="PermissionContextBatch.MarkingFor"/> turns that into
    /// <see cref="ProtectiveMarking.FailClosed"/>, so the fail-closed substitution has
    /// exactly one implementation.</para>
    /// </summary>
    private async Task<Dictionary<Guid, ProtectiveMarking>> LoadMarkingsAsync(
        Guid[] pageIds, CancellationToken cancellationToken)
    {
        if (pageIds.Length == 0)
        {
            return [];
        }

        var markings = await Markings
            .Include(m => m.Countries)
            .Include(m => m.Selectors)
            .Where(m => pageIds.Contains(m.PageId))
            .ToListAsync(cancellationToken);
        return markings.ToDictionary(m => m.PageId, m => m.ToMarking());
    }
}

/// <summary>
/// One batch's grants, restrictions and markings, indexed for in-memory lookup, plus
/// this instance's selector catalog (design.md §21.15) so every context built from the
/// batch carries it. Every page in the batch is served from these rows; nothing here
/// touches the database.
/// </summary>
internal sealed class PermissionContextBatch
{
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> _grantsBySpaceId;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> _restrictionsByPageId;
    private readonly IReadOnlyDictionary<Guid, ProtectiveMarking> _markingsByPageId;
    private readonly SelectorCatalog _catalog;

    internal PermissionContextBatch(
        IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> grantsBySpaceId,
        IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> restrictionsByPageId,
        IReadOnlyDictionary<Guid, ProtectiveMarking> markingsByPageId,
        SelectorCatalog catalog)
    {
        _grantsBySpaceId = grantsBySpaceId;
        _restrictionsByPageId = restrictionsByPageId;
        _markingsByPageId = markingsByPageId;
        _catalog = catalog;
    }

    /// <summary>A space with no grants yields an empty list, which denies (§6.3).</summary>
    public IReadOnlyList<AccessRule> GrantsFor(Guid spaceId) => _grantsBySpaceId.GetValueOrDefault(spaceId, []);

    /// <summary>
    /// THE fail-closed substitution for a page whose marking row is missing (design.md
    /// §21): TOP SECRET, not "unmarked". It lives here and in
    /// <see cref="PermissionContextLoader.LoadMarkingAsync"/> and nowhere else, so a
    /// consumer never gets the chance to decide what a null marking means.
    /// </summary>
    public ProtectiveMarking MarkingFor(Guid pageId) =>
        _markingsByPageId.GetValueOrDefault(pageId) ?? ProtectiveMarking.FailClosed;

    /// <summary>The inputs for one page of the batch, chain ordered per the loader's doc.</summary>
    public PagePermissionContext For(PermissionSubject subject, bool isReplicaSpace) =>
        new(GrantsFor(subject.SpaceId), Order(subject.ChainPageIds(), _restrictionsByPageId), isReplicaSpace,
            MarkingFor(subject.PageId), _catalog);

    /// <summary>
    /// THE chain ordering (design.md §6.7, and the input contract on
    /// <see cref="EffectivePermissionCalculator.Explain"/>): root-most ancestor first,
    /// the page's own rules last, CreatedAtUtc then Id within one page.
    /// </summary>
    internal static IReadOnlyList<AccessRule> Order(
        IReadOnlyList<Guid> chainPageIds, IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> restrictionsByPageId)
    {
        var ordered = new List<AccessRule>();
        foreach (var chainPageId in chainPageIds)
        {
            if (restrictionsByPageId.TryGetValue(chainPageId, out var rules))
            {
                ordered.AddRange(rules.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id));
            }
        }

        return ordered;
    }
}

/// <summary>
/// One page's authorization inputs, ready for the calculator. Construct only through
/// <see cref="PermissionContextLoader"/> — the ordering contract lives there. The
/// catalog rides along from <c>RocketWikiDbContext.SelectorCatalog</c> so a context can
/// be turned into <see cref="PermissionInputs"/> without any caller supplying it — and
/// therefore without any caller being able to supply the wrong one.
/// </summary>
internal sealed record PagePermissionContext(
    IReadOnlyList<AccessRule> SpaceGrants,
    IReadOnlyList<AccessRule> ChainRestrictions,
    bool IsReplicaSpace,
    ProtectiveMarking Marking,
    SelectorCatalog Catalog)
{
    public PermissionInputs ToInputs() => new(SpaceGrants, ChainRestrictions, IsReplicaSpace, Marking, Catalog);

    public EffectivePermission Compute(Principal principal) =>
        EffectivePermissionCalculator.Compute(ToInputs(), principal);

    public EffectivePermissionExplanation Explain(Principal principal) =>
        EffectivePermissionCalculator.Explain(ToInputs(), principal);
}
