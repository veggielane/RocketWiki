using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of <see cref="IPagePermissionReadService"/> (design.md
/// §6.6/§8). Lives in RocketWiki.Data for the same reason PageReadService does: it
/// needs RocketWikiDbContext directly. Needs the local instance id (like PageService)
/// because canEdit/canComment honor the replica invariant (§12) — which
/// IPageReadService's view-only path never had to care about.
///
/// Every permission verdict here comes from <see cref="EffectivePermissionCalculator"/>;
/// this class only assembles the rows the calculator needs — in a constant number of
/// queries per call, since the batch path exists precisely so `children { canEdit }`
/// over N pages is not N ancestor walks.
/// </summary>
public class PagePermissionReadService : IPagePermissionReadService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;
    private readonly PermissionContextLoader _permissions;

    public PagePermissionReadService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db);
    }

    public async Task<IReadOnlyDictionary<Guid, PagePermissionFacts>> GetPermissionFactsAsync(
        IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, PagePermissionFacts>();
        if (pageIds.Count == 0)
        {
            return result;
        }

        // Four queries per batch, independent of page count: the pages, their spaces,
        // then the loader's two (those spaces' grants, and every restriction attached to
        // any page-or-ancestor in the batch). The per-page work below is pure in-memory
        // rule evaluation.
        var ids = pageIds.Distinct().ToArray();
        var pages = await _db.Pages.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        if (pages.Count == 0)
        {
            return result;
        }

        var spaceIds = pages.Select(p => p.SpaceId).Distinct().ToArray();
        var spaces = await _db.Spaces
            .Where(s => spaceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var permissions = await _permissions.LoadBatchAsync(
            pages.Select(PermissionSubject.For).ToList(), cancellationToken);

        foreach (var page in pages)
        {
            if (!spaces.TryGetValue(page.SpaceId, out var space))
            {
                // FK-impossible in practice; omitting the entry fails closed.
                continue;
            }

            // Explain rather than Compute, for two deliberate reasons: it hands back
            // the space role AND the verdict in one pass (canManageAccess needs the
            // role for §6.5.2's gate), and it does not feed the permission-check
            // histogram — that metric tracks the enforcement gate (the read service
            // already recorded this page's canView once), and counting the derived
            // canEdit/canComment facts again would double every page view in it.
            // Explain's verdict is pinned by test to be identical to Compute's.
            var explanation = permissions
                .For(PermissionSubject.For(page), space.IsReplicaOf(_localInstanceId))
                .Explain(principal);

            result[page.Id] = new PagePermissionFacts(
                explanation.Permission,
                explanation.SpaceRole,
                explanation.IsReplicaSpace,
                explanation.HasSpaceAccess);
        }

        return result;
    }

    public async Task<ReadResult<PagePermissionExplanation>> ExplainAsync(
        Guid pageId, Principal caller, Principal subject, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return new ReadResult<PagePermissionExplanation>.NotFound();
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new ReadResult<PagePermissionExplanation>.NotFound();
        }

        var context = await _permissions.LoadAsync(page, space.IsReplicaOf(_localInstanceId), cancellationToken);

        // The CALLER's gate first, via the enforcement path (Compute, not Explain):
        // an inspector the caller can point at a page they cannot view would be the
        // §6.5 read-around this design forbids — it would confirm the page exists and
        // hand over its rules and ancestor titles. Denied travels out with the
        // caller's own failing reason for the audit row (§6.7/§7) and collapses to
        // null at the resolver, exactly like a denied page read.
        var callerPermission = context.Compute(caller);
        if (!callerPermission.CanView)
        {
            return new ReadResult<PagePermissionExplanation>.Denied(callerPermission.ViewDenialReason ?? EffectivePermissionCalculator.NoSpaceAccessReason);
        }

        var titlesByPageId = await LoadChainTitlesAsync(page, caller, context.SpaceGrants, context.ChainRestrictions, cancellationToken);
        var explanation = context.Explain(subject);

        // Display only, never a decision input (design.md §6.1): the local mirror row
        // for the subject, when one exists. A what-if principal an admin typed in has
        // no row; fall back to the id itself.
        var subjectDisplayName = await _db.Users
            .Where(u => u.Subject == subject.UserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? subject.UserId;

        return new ReadResult<PagePermissionExplanation>.Found(new PagePermissionExplanation(
            subject.UserId,
            subjectDisplayName,
            explanation.HasSpaceAccess,
            explanation.GrantedSelectors,
            explanation.SpaceRole,
            explanation.IsReplicaSpace,
            explanation.Permission,
            explanation.ViewGates,
            explanation.EditGates,
            WithTitles(explanation.ViewRestrictions, titlesByPageId),
            WithTitles(explanation.EditRestrictions, titlesByPageId)));
    }

    public async Task<IReadOnlyList<PageRestrictionDetail>> GetRestrictionsAsync(
        Guid pageId, Principal caller, bool callerIsInstanceAdmin, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return [];
        }

        var grants = await _permissions.LoadSpaceGrantsAsync(page.SpaceId, cancellationToken);

        // design.md §6.5.2 via the shared gate — the exact check AccessRuleService's
        // create/update/delete enforce, not a re-derivation. Non-managers get the
        // same empty list a page with no restrictions yields (see the interface doc
        // for why that's a pruned listing, not an auditable denial). A manager needs
        // no access grant to manage (§6.5.2), which is exactly why the chain titles
        // below are gated separately: the rules are theirs to see, the titles of pages
        // they cannot read are not.
        var role = EffectivePermissionCalculator.ComputeSpaceRole(grants, caller);
        if (!RuleManagementGate.CanManageRules(role, callerIsInstanceAdmin))
        {
            return [];
        }

        var rules = await _permissions.LoadOrderedRestrictionsAsync(
            PermissionSubject.For(page).ChainPageIds(), cancellationToken);
        var titlesByPageId = await LoadChainTitlesAsync(page, caller, grants, rules, cancellationToken);

        var editorIds = rules.Select(r => r.UpdatedByUserId).Distinct().ToArray();
        var editorNames = await _db.Users
            .Where(u => editorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        return rules
            .Select(r => new PageRestrictionDetail(
                r.Id,
                r.PageId!.Value,
                titlesByPageId.GetValueOrDefault(r.PageId.Value, string.Empty),
                Inherited: r.PageId.Value != page.Id,
                r.Action!.Value,
                r.ExpressionJson,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                editorNames.GetValueOrDefault(r.UpdatedByUserId)))
            .ToList();
    }

    private static IReadOnlyList<ExplainedRestriction> WithTitles(
        IReadOnlyList<RestrictionCheckDetail> checks, IReadOnlyDictionary<Guid, string> titlesByPageId) =>
        checks
            .Select(c => new ExplainedRestriction(
                c.RuleId, c.PageId, titlesByPageId.GetValueOrDefault(c.PageId, string.Empty),
                c.Action, c.ExpressionJson, c.Passed))
            .ToList();

    /// <summary>
    /// Titles for the page + its ancestors, two queries. Includes soft-deleted ancestors'
    /// (IgnoreQueryFilters) so a rule row is never rendered with a blank owner just
    /// because its page is in the trash — restriction accumulation itself only ever runs
    /// over live pages.
    ///
    /// <para><b>A chain page the caller could not view contributes an EMPTY title</b>
    /// (design.md §21.8/§6.4.1), decided by the FULL view gate for that page — space
    /// access, then the calculator's own ladder (<see cref="EffectivePermissionCalculator.EvaluateViewGates"/>:
    /// level, selector eligibility, selector grant, caveat, and the restrictions
    /// accumulated down to that page) — never a subset. Restrictions accumulate down the
    /// tree but markings do not (§21.5 — "a child may legitimately sit above or below its
    /// parent", and re-marking a parent does not re-mark the subtree), so passing canView
    /// on the subject page says nothing about its ancestors' markings. Without this, a
    /// caller viewing an OFFICIAL child could read a SECRET parent's title straight off
    /// the inspector or the restriction listing — a page the tree prunes and every other
    /// read path treats as absent. §6.4.1 already treats titles as the sensitive part
    /// ("the refusal says how many pages blocked it, not which ones, since their titles
    /// may themselves be restricted"); this is that rule, applied to the one surface that
    /// names an ancestor. A caller with no access grant at all — a manager listing the
    /// rules of a space they cannot read (§6.5.2) — gets every title withheld.</para>
    ///
    /// <para>Only the TITLE is withheld. The rule id, page id, action, expression and
    /// pass/fail all still travel: they are what the inspector exists to explain, the
    /// caller is already subject to that rule, and a rule the caller cannot see the
    /// reasoning for is exactly the "least useful answer" §6.6 set out to avoid.</para>
    /// </summary>
    /// <param name="orderedChainRestrictions">The chain's restrictions root-most first
    /// (the loader's contract); the prefix up to each chain page is what that page's own
    /// canView would have evaluated.</param>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadChainTitlesAsync(
        Page page,
        Principal caller,
        IReadOnlyList<AccessRule> spaceGrants,
        IReadOnlyList<AccessRule> orderedChainRestrictions,
        CancellationToken cancellationToken)
    {
        var chainIds = PermissionSubject.For(page).ChainPageIds().ToArray();
        var titles = await _db.Pages.IgnoreQueryFilters()
            .Where(p => chainIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);

        var access = EffectivePermissionCalculator.ComputeSpaceAccess(spaceGrants, caller);
        if (access is null)
        {
            foreach (var chainId in chainIds)
            {
                if (titles.ContainsKey(chainId))
                {
                    titles[chainId] = string.Empty;
                }
            }

            return titles;
        }

        var markings = await _permissions.LoadMarkingsForAsync(chainIds, cancellationToken);
        var accumulated = new List<AccessRule>();
        foreach (var chainId in chainIds)
        {
            accumulated.AddRange(orderedChainRestrictions.Where(r => r.PageId == chainId));
            if (!titles.ContainsKey(chainId))
            {
                continue;
            }

            var gates = EffectivePermissionCalculator.EvaluateViewGates(
                access, markings[chainId], accumulated, _db.SelectorCatalog, caller, shortCircuit: true);
            if (gates.Any(g => !g.Passed))
            {
                titles[chainId] = string.Empty;
            }
        }

        return titles;
    }
}
