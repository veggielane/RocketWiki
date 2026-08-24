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
/// issues exactly two queries — one for the grants of every space involved, one for the
/// restrictions of every page-or-ancestor involved — regardless of batch size; the
/// per-page work afterwards is pure in-memory rule evaluation. List paths (search
/// post-filtering, label listings, subtree checks, notification re-checks, per-child
/// permission facts) use it instead of looping over the per-page load, which costs two
/// queries each.</para>
///
/// <para>Fail closed (design.md §6.3): a space with no grant rows yields an empty grant
/// list, which <see cref="EffectivePermissionCalculator.ComputeSpaceRole"/> turns into
/// no role, which denies. A page id with no restriction rows contributes nothing to the
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

    /// <summary>Grants + ordered restriction chain for one page, in two queries.</summary>
    /// <param name="isReplicaSpace">
    /// The space's own <see cref="Space.IsReplicaOf"/> result. Not derived here: a
    /// view-only caller has no local instance id to compare against, and canView is
    /// unaffected by it (design.md §6.4 — the replica invariant only ever suppresses
    /// canEdit), so those callers pass false deliberately.
    /// </param>
    public Task<PagePermissionContext> LoadAsync(Page page, bool isReplicaSpace, CancellationToken cancellationToken) =>
        LoadAsync(page.SpaceId, PermissionSubject.For(page).ChainPageIds(), isReplicaSpace, cancellationToken);

    /// <summary>
    /// Grants + ordered restriction chain for an explicit chain of page ids, in two
    /// queries. For callers whose chain is not one page's own — a page being created
    /// under a parent (it inherits the parent's chain), or a move's destination chain.
    /// <paramref name="chainPageIds"/> must already be root-most first; see the class doc.
    /// </summary>
    public async Task<PagePermissionContext> LoadAsync(
        Guid spaceId, IReadOnlyList<Guid> chainPageIds, bool isReplicaSpace, CancellationToken cancellationToken)
    {
        var grants = await LoadSpaceGrantsAsync(spaceId, cancellationToken);
        var restrictions = await LoadOrderedRestrictionsAsync(chainPageIds, cancellationToken);
        return new PagePermissionContext(grants, restrictions, isReplicaSpace);
    }

    /// <summary>Every SpaceGrant rule for one space — one query.</summary>
    public async Task<IReadOnlyList<AccessRule>> LoadSpaceGrantsAsync(Guid spaceId, CancellationToken cancellationToken) =>
        await Rules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == spaceId)
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

    /// <summary>Two queries for the whole batch — see the class doc's batching guarantee.</summary>
    public Task<PermissionContextBatch> LoadBatchAsync(
        IReadOnlyCollection<PermissionSubject> subjects, CancellationToken cancellationToken) =>
        LoadBatchAsync(subjects, [], cancellationToken);

    /// <summary>
    /// As <see cref="LoadBatchAsync(IReadOnlyCollection{PermissionSubject}, CancellationToken)"/>,
    /// also loading grants for spaces that carry no page in the batch — for callers that
    /// additionally gate space-scoped rows on "holds any role in that space". Still two
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
                .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId != null && spaceIds.Contains(r.SpaceId.Value))
                .ToListAsync(cancellationToken);

        return new PermissionContextBatch(
            grants.GroupBy(r => r.SpaceId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<AccessRule>)g.ToList()),
            await LoadRestrictionsAsync(chainIds, cancellationToken));
    }

    /// <summary>
    /// Restrictions-only batch — ONE query — for a single-space listing whose caller
    /// already holds that space's grants because it gated the space role before it
    /// looked at any page (design.md §6.7). Re-querying them here would be the one
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
            await LoadRestrictionsAsync(chainIds, cancellationToken));
    }

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
}

/// <summary>
/// One batch's grants and restrictions, indexed for in-memory lookup. Every page in the
/// batch is served from these rows; nothing here touches the database.
/// </summary>
internal sealed class PermissionContextBatch
{
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> _grantsBySpaceId;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> _restrictionsByPageId;

    internal PermissionContextBatch(
        IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> grantsBySpaceId,
        IReadOnlyDictionary<Guid, IReadOnlyList<AccessRule>> restrictionsByPageId)
    {
        _grantsBySpaceId = grantsBySpaceId;
        _restrictionsByPageId = restrictionsByPageId;
    }

    /// <summary>A space with no grants yields an empty list, which denies (§6.3).</summary>
    public IReadOnlyList<AccessRule> GrantsFor(Guid spaceId) => _grantsBySpaceId.GetValueOrDefault(spaceId, []);

    /// <summary>The inputs for one page of the batch, chain ordered per the loader's doc.</summary>
    public PagePermissionContext For(PermissionSubject subject, bool isReplicaSpace) =>
        new(GrantsFor(subject.SpaceId), Order(subject.ChainPageIds(), _restrictionsByPageId), isReplicaSpace);

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
/// <see cref="PermissionContextLoader"/> — the ordering contract lives there.
/// </summary>
internal sealed record PagePermissionContext(
    IReadOnlyList<AccessRule> SpaceGrants,
    IReadOnlyList<AccessRule> ChainRestrictions,
    bool IsReplicaSpace)
{
    public EffectivePermission Compute(Principal principal) =>
        EffectivePermissionCalculator.Compute(SpaceGrants, ChainRestrictions, IsReplicaSpace, principal);

    public EffectivePermissionExplanation Explain(Principal principal) =>
        EffectivePermissionCalculator.Explain(SpaceGrants, ChainRestrictions, IsReplicaSpace, principal);
}
