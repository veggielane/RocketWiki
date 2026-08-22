using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of ISearchService. One class, self-detecting provider
/// (same pattern as RocketWikiDbContext's SQLite identity workaround) rather than two
/// registered implementations, so DI stays a one-line "register ISearchService" for
/// whoever wires it up - not their decision to pick a provider-specific class.
///
/// design.md §9.1 (milestone 4) scope only: keyword search over Title/CurrentContent.
/// The hybrid FTS+vector fusion in §9.2/9.3 needs an embedding pipeline that doesn't
/// exist yet (milestone 7) and is deliberately out of scope here.
/// </summary>
public class SearchService : ISearchService
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const int OverFetchMultiplier = 4; // design.md §9.3: over-fetch top-K before permission filtering
    private const int SnippetLength = 200;

    private readonly RocketWikiDbContext _db;

    public SearchService(RocketWikiDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request, Principal principal, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || maxResults <= 0)
        {
            return Array.Empty<SearchHit>();
        }

        var overFetchCount = maxResults * OverFetchMultiplier;

        var candidates = _db.Database.ProviderName == SqlServerProviderName
            ? await SearchViaFullTextAsync(request.Query, request.SpaceKey, overFetchCount, cancellationToken)
            : await SearchViaLikeAsync(request.Query, request.SpaceKey, overFetchCount, cancellationToken);

        if (candidates.Count == 0)
        {
            return Array.Empty<SearchHit>();
        }

        if (request.Labels is { Count: > 0 })
        {
            candidates = await FilterByLabelsAsync(candidates, request.Labels, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<SearchHit>();
        }

        return await FilterByCanViewAsync(candidates, principal, maxResults, cancellationToken);
    }

    /// <summary>
    /// TODO(sql-server, unexercised): runs CONTAINSTABLE against the FTS index created
    /// in the migration (Page(Title, CurrentContent)). There is no SQL Server
    /// Testcontainers suite yet (design.md §14's third tier), so this path is written
    /// carefully but has never actually run against a real SQL Server instance -
    /// verify it there before trusting it in production.
    /// </summary>
    private async Task<List<SearchCandidate>> SearchViaFullTextAsync(
        string query, string? spaceKey, int overFetchCount, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQuery<PageSearchRow>($"""
            SELECT TOP ({overFetchCount}) p.Id AS Id, p.Title AS Title, p.SpaceId AS SpaceId,
                   s.[Key] AS SpaceKey, p.CurrentContent AS CurrentContent, p.AncestorPath AS AncestorPath
            FROM Pages p
            INNER JOIN CONTAINSTABLE(Pages, (Title, CurrentContent), {query}) AS ft ON p.Id = ft.[KEY]
            INNER JOIN Spaces s ON s.Id = p.SpaceId
            WHERE p.IsDeleted = 0
              AND ({spaceKey} IS NULL OR s.[Key] = {spaceKey})
            ORDER BY ft.RANK DESC
            """).ToListAsync(cancellationToken);

        return rows.Select(r => new SearchCandidate(r.Id, r.Title, r.SpaceId, r.SpaceKey, r.CurrentContent, r.AncestorPath)).ToList();
    }

    /// <summary>design.md §14's prescribed SQLite fallback: LIKE, ordered by recency since there's no natural relevance rank.</summary>
    private async Task<List<SearchCandidate>> SearchViaLikeAsync(
        string query, string? spaceKey, int overFetchCount, CancellationToken cancellationToken)
    {
        var pattern = $"%{query}%";
        var pagesQuery = _db.Pages.Where(p => EF.Functions.Like(p.Title, pattern) || EF.Functions.Like(p.CurrentContent, pattern));

        if (spaceKey is not null)
        {
            pagesQuery = pagesQuery.Where(p => p.Space!.Key == spaceKey);
        }

        var rows = await pagesQuery
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Take(overFetchCount)
            .Select(p => new { p.Id, p.Title, p.SpaceId, SpaceKey = p.Space!.Key, p.CurrentContent, p.AncestorPath })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SearchCandidate(r.Id, r.Title, r.SpaceId, r.SpaceKey, r.CurrentContent, r.AncestorPath)).ToList();
    }

    private async Task<List<SearchCandidate>> FilterByLabelsAsync(
        List<SearchCandidate> candidates, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var candidateIds = candidates.Select(c => c.PageId).ToList();
        var matchingPageIds = await _db.PageLabels
            .Where(pl => candidateIds.Contains(pl.PageId) && labels.Contains(pl.Label!.Name))
            .Select(pl => pl.PageId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return candidates.Where(c => matchingPageIds.Contains(c.PageId)).ToList();
    }

    /// <summary>
    /// design.md §9.3: over-fetch, then filter by canView, preserving the search
    /// mechanism's own rank/recency order - stops as soon as maxResults visible hits
    /// are found rather than scoring every candidate.
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> FilterByCanViewAsync(
        List<SearchCandidate> candidates, Principal principal, int maxResults, CancellationToken cancellationToken)
    {
        var spaceIds = candidates.Select(c => c.SpaceId).Distinct().ToArray();
        var spaceGrantsBySpace = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && spaceIds.Contains(r.SpaceId!.Value))
            .ToListAsync(cancellationToken);

        var relevantRestrictionPageIds = candidates
            .SelectMany(c => ParseAncestorIds(c.AncestorPath).Append(c.PageId))
            .Distinct()
            .ToArray();
        var restrictions = relevantRestrictionPageIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && relevantRestrictionPageIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        var hits = new List<SearchHit>();
        foreach (var candidate in candidates)
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            var spaceGrants = spaceGrantsBySpace.Where(r => r.SpaceId == candidate.SpaceId).ToList();
            var ancestorIds = new HashSet<Guid>(ParseAncestorIds(candidate.AncestorPath)) { candidate.PageId };
            var applicableRestrictions = restrictions.Where(r => ancestorIds.Contains(r.PageId!.Value)).ToList();

            var permission = EffectivePermissionCalculator.Compute(spaceGrants, applicableRestrictions, isReplicaSpace: false, principal);
            if (permission.CanView)
            {
                hits.Add(new SearchHit(candidate.PageId, candidate.Title, candidate.SpaceKey, BuildSnippet(candidate.CurrentContent)));
            }
        }

        return hits;
    }

    private static string BuildSnippet(string content) =>
        content.Length <= SnippetLength ? content : content[..SnippetLength] + "…";

    private static IEnumerable<Guid> ParseAncestorIds(string ancestorPath) =>
        string.IsNullOrEmpty(ancestorPath) || ancestorPath == "/"
            ? Array.Empty<Guid>()
            : ancestorPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse);

    private sealed record SearchCandidate(Guid PageId, string Title, Guid SpaceId, string SpaceKey, string CurrentContent, string AncestorPath);

    private sealed class PageSearchRow
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public Guid SpaceId { get; init; }
        public string SpaceKey { get; init; } = string.Empty;
        public string CurrentContent { get; init; } = string.Empty;
        public string AncestorPath { get; init; } = string.Empty;
    }
}
