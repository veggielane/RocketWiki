using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Query;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// Executes RQL (design.md §22): parse → validate → compile to a parameterized EF query →
/// run it over a bounded candidate set → post-filter every candidate through the ordinary
/// per-page <c>canView</c> gate → cap.
///
/// <para><b>The permission filter is a post-filter, and the query has no say in it.</b>
/// This is the same shape <see cref="SearchService"/> and <c>LabelService.GetPagesByLabelAsync</c>
/// use, for the same reason (design.md §6.4.2/§6.7): pages matching a flat filter have no
/// prune-the-subtree shortcut, so every candidate is evaluated against its own ancestor
/// restriction chain and its own protective marking. Clearance and eyes-only filtering
/// arrive for free through <see cref="PagePermissionContext.Compute"/> — there is no
/// second implementation of §21's gate here, deliberately.</para>
///
/// <para><b>Two narrowings happen before the post-filter, and neither can widen.</b> The
/// candidate set is restricted to spaces the caller holds <i>some</i> role in, because a
/// space role is a necessary condition of <c>canView</c> (design.md §6.4) — a page in a
/// space you hold no role in can never pass, so excluding it early changes no answer and is
/// what makes the candidate cap meaningful rather than a lottery over the whole estate. And
/// the compiled predicate only ever removes candidates. Nothing in an RQL string reaches
/// the permission computation.</para>
///
/// <para><b>Nothing is reported about what was filtered out.</b> No candidate count, no
/// "capped" flag, no total-before-filtering. Each of those would let a caller learn that
/// pages they cannot see exist, which is exactly what §6.7 forbids — the cap is stated in
/// the documentation and the schema, not inferred from the response.</para>
/// </summary>
public sealed class PageQueryService : IPageQueryService
{
    /// <summary>
    /// Rows fetched from the database before permission filtering. Bounded like every other
    /// list path: an unbounded candidate scan is the one way a filter language becomes a
    /// denial-of-service. Set well above <c>MaxPageQueryResults</c> so a restriction-heavy
    /// result set does not come back looking empty — the same over-fetch reasoning
    /// <see cref="SearchService"/> applies (design.md §9.3).
    /// </summary>
    private const int MaxCandidates = 500;

    private readonly RocketWikiDbContext _db;
    private readonly PermissionContextLoader _permissions;
    private readonly TimeProvider _timeProvider;

    public PageQueryService(RocketWikiDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _permissions = new PermissionContextLoader(db, noTracking: true);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PageQueryOutcome> ExecuteAsync(
        string query, Principal principal, int maxResults, CancellationToken cancellationToken = default)
    {
        var parsed = Rql.Parse(query);
        if (parsed.Query is null)
        {
            return PageQueryOutcome.Invalid(parsed.Errors);
        }

        if (maxResults <= 0)
        {
            return PageQueryOutcome.Empty;
        }

        // design.md §22: now() is resolved ONCE per execution, so two now()s in one query
        // can never disagree about what "now" was.
        var resolved = RqlNowResolver.Resolve(parsed.Query, _timeProvider.GetUtcNow().UtcDateTime);

        var visibleSpaces = await LoadSpacesWithAnyRoleAsync(principal, cancellationToken);
        if (visibleSpaces.Count == 0)
        {
            return PageQueryOutcome.Empty;
        }

        var context = new RqlCompilationContext(
            visibleSpaces,
            await ResolveCreatorSubjectsAsync(resolved, principal, cancellationToken),
            principal.UserId);

        var spaceIds = visibleSpaces.Values.Distinct().ToList();
        var candidates = await OrderCandidates(
                _db.Pages
                    .AsNoTracking()
                    .Where(p => spaceIds.Contains(p.SpaceId))
                    .Where(RqlQueryCompiler.Compile(resolved.Where, context)),
                resolved.OrderBy)
            .Take(MaxCandidates)
            .Select(p => new CandidateRow(p.Id, p.SpaceId, p.AncestorPath))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return PageQueryOutcome.Empty;
        }

        return new PageQueryOutcome(
            await FilterByCanViewAsync(candidates, principal, maxResults, cancellationToken), []);
    }

    /// <summary>
    /// The spaces the caller holds any role in, keyed by space key (ordinal). Two jobs in
    /// one query: it narrows the candidate scan, and it <i>is</i> the map
    /// <see cref="RqlQueryCompiler"/> resolves <c>space = "…"</c> against — which is what
    /// makes an invisible space compile identically to a nonexistent one.
    ///
    /// <para>Uses <see cref="PermissionContextLoader"/>'s <c>additionalSpaceIds</c> overload:
    /// no page subject exists yet at this point, so every space here is one that "carries no
    /// page in the batch", which is precisely what that overload is for
    /// (<see cref="NotificationReadModelService"/> is the other caller). One query.</para>
    ///
    /// <para>Ordinal keying, and it stays ordinal now that space keys are
    /// case-insensitive: both sides of the comparison are canonical rather than
    /// case-folded at compare time. <c>Space.Key</c> is stored canonically
    /// (RocketWikiDbContext), and <c>RqlQueryCompiler.CompileSpace</c> canonicalizes the
    /// key an author wrote before looking it up here — so <c>space = "eng"</c> finds ENG
    /// without this map ever holding a second, looser notion of space identity. That was
    /// the thing worth avoiding, not the case-insensitivity itself.</para>
    /// </summary>
    private async Task<Dictionary<string, Guid>> LoadSpacesWithAnyRoleAsync(
        Principal principal, CancellationToken cancellationToken)
    {
        // Soft-deleted AND archived spaces are excluded by SpaceConfiguration's query filter
        // (archive sets IsDeleted; design.md §6.5.1), so neither can contribute candidates.
        var spaces = await _db.Spaces
            .AsNoTracking()
            .Select(s => new { s.Id, s.Key })
            .ToListAsync(cancellationToken);

        if (spaces.Count == 0)
        {
            return [];
        }

        var grants = await _permissions.LoadBatchAsync(
            [], spaces.Select(s => s.Id).ToList(), cancellationToken);

        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var space in spaces)
        {
            if (EffectivePermissionCalculator.ComputeSpaceRole(grants.GrantsFor(space.Id), principal) is not null)
            {
                result[space.Key] = space.Id;
            }
        }

        return result;
    }

    /// <summary>
    /// Maps every subject named by a <c>creator</c> predicate (plus the caller's own, for
    /// <c>currentUser()</c>) to a local <c>User.Id</c>, in one query.
    ///
    /// <para>This reads the local <c>User</c> mirror, which design.md §6.1 forbids
    /// <i>authorization</i> from doing. It is not authorization: it is the same
    /// display/foreign-key lookup <c>IActingUserAccessor</c> exists for, used to turn a name
    /// into a row id for a content filter. The permission decision below runs entirely off
    /// the token-built <see cref="Principal"/> and never consults this map.</para>
    ///
    /// <para>A subject with no row simply does not appear, and the compiler turns that into a
    /// never-matches predicate — an unknown user is indistinguishable from one who has
    /// created nothing visible, exactly as an unknown space key is indistinguishable from an
    /// invisible space.</para>
    /// </summary>
    private async Task<Dictionary<string, Guid>> ResolveCreatorSubjectsAsync(
        RqlQuery query, Principal principal, CancellationToken cancellationToken)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        CollectCreatorSubjects(query.Where, principal, subjects);

        if (subjects.Count == 0)
        {
            return [];
        }

        var wanted = subjects.ToList();
        var rows = await _db.Users
            .AsNoTracking()
            .Where(u => u.Subject != null && wanted.Contains(u.Subject))
            .Select(u => new { u.Subject, u.Id })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.Subject!, r => r.Id, StringComparer.Ordinal);
    }

    private static void CollectCreatorSubjects(RqlNode node, Principal principal, HashSet<string> subjects)
    {
        switch (node)
        {
            case RqlNode.And and:
                foreach (var child in and.Children)
                {
                    CollectCreatorSubjects(child, principal, subjects);
                }

                break;

            case RqlNode.Or or:
                foreach (var child in or.Children)
                {
                    CollectCreatorSubjects(child, principal, subjects);
                }

                break;

            case RqlNode.Not not:
                CollectCreatorSubjects(not.Child, principal, subjects);
                break;

            case RqlNode.Predicate { Field: RqlField.Creator } predicate:
                foreach (var value in predicate.Values)
                {
                    switch (value)
                    {
                        case RqlValue.Text text:
                            subjects.Add(text.Value);
                            break;
                        case RqlValue.CurrentUser:
                            subjects.Add(principal.UserId);
                            break;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// design.md §22's ORDER BY, defaulting to <c>updated DESC</c> when the author wrote
    /// none. Always tie-broken on <c>Id</c>: without a total order, two pages sharing a
    /// timestamp (every bulk import produces plenty) could swap places between the request
    /// that fetched page one of a connection and the request that fetched page two,
    /// duplicating one row and dropping another.
    /// </summary>
    private static IOrderedQueryable<Page> OrderCandidates(
        IQueryable<Page> pages, IReadOnlyList<RqlOrderItem> orderBy)
    {
        if (orderBy.Count == 0)
        {
            return pages.OrderByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id);
        }

        IOrderedQueryable<Page>? ordered = null;
        foreach (var item in orderBy)
        {
            ordered = item.Field switch
            {
                RqlField.Title => Apply(ordered, pages, p => p.Title, item.Direction),
                RqlField.Created => Apply(ordered, pages, p => p.CreatedAtUtc, item.Direction),
                RqlField.Updated => Apply(ordered, pages, p => p.UpdatedAtUtc, item.Direction),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(orderBy), item.Field, "RqlValidator must reject unsortable ORDER BY fields."),
            };
        }

        return ordered!.ThenBy(p => p.Id);
    }

    private static IOrderedQueryable<Page> Apply<TKey>(
        IOrderedQueryable<Page>? ordered,
        IQueryable<Page> pages,
        System.Linq.Expressions.Expression<Func<Page, TKey>> selector,
        RqlSortDirection direction)
    {
        if (ordered is null)
        {
            return direction == RqlSortDirection.Descending
                ? pages.OrderByDescending(selector)
                : pages.OrderBy(selector);
        }

        return direction == RqlSortDirection.Descending
            ? ordered.ThenByDescending(selector)
            : ordered.ThenBy(selector);
    }

    /// <summary>
    /// design.md §6.7: every candidate is checked individually against its own ancestor
    /// restriction chain and its own marking, and a page that fails is <b>absent</b> — not a
    /// redacted row, not a gap in the ordering, and not implied by any count. Three queries
    /// for the whole candidate set; the per-page work is in-memory rule evaluation.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> FilterByCanViewAsync(
        IReadOnlyList<CandidateRow> candidates,
        Principal principal,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var subjects = candidates.Select(c => c.Subject).ToList();
        var batch = await _permissions.LoadBatchAsync(subjects, cancellationToken);

        var visible = new List<Guid>(Math.Min(maxResults, candidates.Count));
        foreach (var candidate in candidates)
        {
            if (visible.Count >= maxResults)
            {
                break;
            }

            // Replica status is irrelevant to canView (design.md §6.4) - the replica
            // invariant only ever suppresses canEdit.
            if (batch.For(candidate.Subject, isReplicaSpace: false).Compute(principal).CanView)
            {
                visible.Add(candidate.PageId);
            }
        }

        return visible;
    }

    /// <summary>A candidate projected straight from SQL — no <see cref="Page"/> entity is
    /// materialized, so the restriction chain comes from the materialized path, exactly as
    /// the search post-filter does it.</summary>
    private readonly record struct CandidateRow(Guid PageId, Guid SpaceId, string AncestorPath)
    {
        public PermissionSubject Subject => new(PageId, SpaceId, AncestorPath);
    }
}
