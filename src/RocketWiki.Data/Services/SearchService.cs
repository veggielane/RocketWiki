using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Search;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of ISearchService. One class, self-detecting provider
/// (same pattern as RocketWikiDbContext's SQLite identity workaround) rather than two
/// registered implementations, so DI stays a one-line "register ISearchService" for
/// whoever wires it up - not their decision to pick a provider-specific class.
///
/// design.md §9 (milestones 4 + 7): hybrid retrieval. Keyword candidates (FTS on SQL
/// Server, LIKE fallback elsewhere) and vector-similarity candidates are each
/// over-fetched, fused with reciprocal rank fusion, and only THEN permission-filtered
/// (§9.3: over-fetching before the filter keeps restriction-heavy result sets from
/// coming back empty). The embedding generator is optional by design - when the
/// endpoint isn't configured, or embedding the query fails, search degrades gracefully
/// to keyword-only (§9.2) rather than erroring.
/// </summary>
public class SearchService : ISearchService
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const int OverFetchMultiplier = 4; // design.md §9.3: over-fetch top-K before permission filtering
    private const int SnippetLength = 200;

    /// <summary>
    /// The standard RRF constant (Cormack et al.): score = Σ 1/(60 + rank). Large enough
    /// that a page ranked well by BOTH signals beats a page ranked first by only one -
    /// which is the property the both-signals-beats-single-signal test pins down.
    /// </summary>
    private const int RrfK = 60;

    private readonly RocketWikiDbContext _db;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly EmbeddingOptions? _embeddingOptions;
    private readonly ILogger<SearchService>? _logger;
    private readonly PermissionContextLoader _permissions;

    public SearchService(
        RocketWikiDbContext db,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        EmbeddingOptions? embeddingOptions = null,
        ILogger<SearchService>? logger = null)
    {
        _db = db;
        _embeddingGenerator = embeddingGenerator;
        _embeddingOptions = embeddingOptions;
        _logger = logger;
        _permissions = new PermissionContextLoader(db);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request, Principal principal, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || maxResults <= 0)
        {
            return Array.Empty<SearchHit>();
        }

        var overFetchCount = maxResults * OverFetchMultiplier;

        var keywordCandidates = _db.Database.ProviderName == SqlServerProviderName
            ? await SearchViaFullTextAsync(request.Query, request.SpaceKey, overFetchCount, cancellationToken)
            : await SearchViaLikeAsync(request.Query, request.SpaceKey, overFetchCount, cancellationToken);

        var vectorHits = await SearchViaVectorsAsync(request.Query, request.SpaceKey, overFetchCount, cancellationToken);

        var (candidates, chunkHints) = await FuseAsync(keywordCandidates, vectorHits, cancellationToken);

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

        return await FilterByCanViewAsync(candidates, chunkHints, request.Query, principal, maxResults, cancellationToken);
    }

    /// <summary>
    /// Runs CONTAINSTABLE against the FTS index created in the InitialCreate migration
    /// (Pages(Title, CurrentContent)), ordered by FTS RANK. The raw user query is first
    /// compiled into a real search condition by <see cref="FullTextQueryBuilder"/> -
    /// passing user text straight into CONTAINSTABLE is a syntax error for any
    /// multi-word query, and a plain term would miss the stemming ("running" finding
    /// "run") that is the whole point of FTS over LIKE. Covered by
    /// tests/RocketWiki.SqlServer.Tests (design.md §14's third tier), which runs only
    /// where Docker is available - in practice CI's sqlserver job; this branch has
    /// never run on a developer machine without a container runtime.
    /// </summary>
    private async Task<List<SearchCandidate>> SearchViaFullTextAsync(
        string query, string? spaceKey, int overFetchCount, CancellationToken cancellationToken)
    {
        var containsCondition = FullTextQueryBuilder.BuildContainsCondition(query);
        if (containsCondition is null)
        {
            return []; // nothing indexable in the query - no keyword candidates
        }

        var rows = await _db.Database.SqlQuery<PageSearchRow>($"""
            SELECT TOP ({overFetchCount}) p.Id AS Id, p.Title AS Title, p.SpaceId AS SpaceId,
                   s.[Key] AS SpaceKey, p.CurrentContent AS CurrentContent, p.AncestorPath AS AncestorPath
            FROM Pages p
            INNER JOIN CONTAINSTABLE(Pages, (Title, CurrentContent), {containsCondition}) AS ft ON p.Id = ft.[KEY]
            INNER JOIN Spaces s ON s.Id = p.SpaceId
            WHERE p.IsDeleted = 0
              AND s.IsDeleted = 0
              AND ({spaceKey} IS NULL OR s.[Key] = {spaceKey})
            ORDER BY ft.RANK DESC, p.Id
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
            // Deterministic order matters beyond aesthetics: the API layer paginates over
            // this sequence with positional cursors, so ties (bulk-imported pages sharing
            // one UpdatedAtUtc) must not reshuffle between "load more" requests.
            .OrderByDescending(p => p.UpdatedAtUtc)
            .ThenBy(p => p.Id)
            .Take(overFetchCount)
            .Select(p => new { p.Id, p.Title, p.SpaceId, SpaceKey = p.Space!.Key, p.CurrentContent, p.AncestorPath })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SearchCandidate(r.Id, r.Title, r.SpaceId, r.SpaceKey, r.CurrentContent, r.AncestorPath)).ToList();
    }

    /// <summary>
    /// Semantic candidates: cosine similarity between the query's embedding and the
    /// stored chunk embeddings, best chunk per page, ranked. Returns empty - degrading
    /// the whole search to keyword-only - when no generator is configured, when the
    /// endpoint fails (§9.2: while the endpoint is down, search degrades gracefully to
    /// FTS-only), and when the index holds another model's vectors (§9.3: an index only
    /// ever contains one model's vectors, enforced here at query time via the Model stamp).
    ///
    /// Two provider branches, one contract (both are EXACT cosine ranking, so every
    /// behavioral test transfers): SQL Server scores in-engine with
    /// VECTOR_DISTANCE('cosine', …) over the native vector(1536) column - the §9.3 path,
    /// proven by tests/RocketWiki.SqlServer.Tests/VectorSearchTests - and SQLite runs
    /// the §14-prescribed in-memory scan over the blob column. A DiskANN-assisted
    /// approximate search would additionally need the preview VECTOR_SEARCH TVF
    /// (VECTOR_DISTANCE never uses a vector index, per its docs); see
    /// AlterPageEmbeddingToNativeVector for why that index is deliberately not created
    /// on boxed SQL Server 2025 yet.
    /// </summary>
    private async Task<List<VectorHit>> SearchViaVectorsAsync(
        string query, string? spaceKey, int overFetchCount, CancellationToken cancellationToken)
    {
        if (_embeddingGenerator is null || _embeddingOptions is null)
        {
            return [];
        }

        float[] queryVector;
        try
        {
            var generated = await _embeddingGenerator.GenerateAsync(
                [query],
                new EmbeddingGenerationOptions { ModelId = _embeddingOptions.Model, Dimensions = _embeddingOptions.Dimensions },
                cancellationToken);
            queryVector = generated[0].Vector.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Exception type only - the query text belongs in the audit row (§7),
            // never in a log message (§15).
            _logger?.LogWarning(ex,
                "Query embedding failed ({ExceptionType}); this search degrades to keyword-only", ex.GetType().Name);
            return [];
        }

        // Only the embedding-endpoint call above is caught: a failure INSIDE the store
        // (e.g. a query vector whose dimensions don't match the vector(1536) column)
        // is a configuration error, not endpoint weather - it propagates and fails the
        // request loudly rather than silently degrading.
        if (_db.Database.ProviderName == SqlServerProviderName)
        {
            return await SearchViaVectorDistanceAsync(queryVector, spaceKey, overFetchCount, cancellationToken);
        }

        // The Page navigation is referenced explicitly so the soft-delete query filters
        // join in and trashed pages/spaces drop out of the scan itself - the fused
        // candidates are re-fetched through filtered DbSets later anyway, but the store
        // should never even score what the model says is gone.
        var embeddingRows = await _db.PageEmbeddings
            .Where(e => e.Model == _embeddingOptions.Model)
            .Where(e => !e.Page!.IsDeleted && !e.Page!.Space!.IsDeleted)
            .Where(e => spaceKey == null || e.Page!.Space!.Key == spaceKey)
            .Select(e => new { e.PageId, e.ChunkIndex, e.Embedding })
            .ToListAsync(cancellationToken);

        // Best chunk per page - one hit per page so RRF fuses page ranks, not chunk ranks.
        var bestPerPage = new Dictionary<Guid, VectorHit>();
        foreach (var row in embeddingRows)
        {
            var similarity = CosineSimilarity(queryVector, row.Embedding);
            if (!bestPerPage.TryGetValue(row.PageId, out var best) || similarity > best.Similarity)
            {
                bestPerPage[row.PageId] = new VectorHit(row.PageId, row.ChunkIndex, similarity);
            }
        }

        return bestPerPage.Values
            .OrderByDescending(h => h.Similarity)
            .ThenBy(h => h.PageId)
            .Take(overFetchCount)
            .ToList();
    }

    /// <summary>
    /// design.md §9.3: the SQL Server branch — VECTOR_DISTANCE('cosine', …) over the
    /// native vector(1536) column, entirely in-engine, so candidate vectors never cross
    /// the wire. Raw SQL for the same reason as SearchViaFullTextAsync (CONTAINSTABLE):
    /// the construct has no provider-agnostic LINQ form, and this class already branches
    /// per provider. Same contract as the in-memory scan: exact cosine, best chunk per
    /// page (ROW_NUMBER), top-N pages, deterministic tie-breaks (distance, then PageId;
    /// chunk ties by ChunkIndex), soft-deleted pages/spaces excluded in the scan itself.
    /// Cosine DISTANCE (0..2) converts to the similarity the fused pipeline records as
    /// similarity = 1 - distance. One deliberate edge divergence: a stored zero vector
    /// has no defined cosine distance — the engine yields NULL and the row is skipped
    /// here, where the in-memory scan ranks it unwinnably last; both mean "never
    /// surfaces", and real embeddings are never zero.
    ///
    /// The query vector travels as a Microsoft.Data.SqlClient SqlVector&lt;float&gt;
    /// parameter — the same type mapping the write path (RocketWikiDbContext's SQL
    /// Server Embedding conversion) uses. If its dimensions don't match the column's,
    /// the engine rejects the query and the error propagates: fail loud, never
    /// fail-open into keyword-only (see caller).
    /// </summary>
    private async Task<List<VectorHit>> SearchViaVectorDistanceAsync(
        float[] queryVector, string? spaceKey, int overFetchCount, CancellationToken cancellationToken)
    {
        var queryParameter = new Microsoft.Data.SqlTypes.SqlVector<float>(queryVector);

        var rows = await _db.Database.SqlQuery<VectorDistanceRow>($"""
            SELECT TOP ({overFetchCount}) ranked.PageId, ranked.ChunkIndex, ranked.Distance
            FROM (
                SELECT e.PageId AS PageId, e.ChunkIndex AS ChunkIndex,
                       VECTOR_DISTANCE('cosine', e.Embedding, {queryParameter}) AS Distance,
                       ROW_NUMBER() OVER (
                           PARTITION BY e.PageId
                           ORDER BY VECTOR_DISTANCE('cosine', e.Embedding, {queryParameter}) ASC, e.ChunkIndex ASC) AS ChunkRank
                FROM PageEmbeddings e
                INNER JOIN Pages p ON p.Id = e.PageId
                INNER JOIN Spaces s ON s.Id = p.SpaceId
                WHERE e.Model = {_embeddingOptions!.Model}
                  AND p.IsDeleted = 0
                  AND s.IsDeleted = 0
                  AND ({spaceKey} IS NULL OR s.[Key] = {spaceKey})
            ) ranked
            WHERE ranked.ChunkRank = 1 AND ranked.Distance IS NOT NULL
            ORDER BY ranked.Distance ASC, ranked.PageId ASC
            """).ToListAsync(cancellationToken);

        return rows.Select(r => new VectorHit(r.PageId, r.ChunkIndex, 1.0 - r.Distance)).ToList();
    }

    /// <summary>
    /// design.md §9.3: reciprocal rank fusion over the two candidate lists, page-level.
    /// Output preserves everything either signal found (a semantic-only hit with zero
    /// keyword overlap survives), ranks pages found by both signals above single-signal
    /// pages, and carries the best-matching chunk index for vector hits so the final
    /// SearchHit can deep-link the section. Pages only the vector index knew about are
    /// materialized through the soft-delete-filtered Pages set - a stale vector row for
    /// a vanished page yields nothing.
    /// </summary>
    private async Task<(List<SearchCandidate> Candidates, Dictionary<Guid, int> ChunkHints)> FuseAsync(
        List<SearchCandidate> keywordCandidates, List<VectorHit> vectorHits, CancellationToken cancellationToken)
    {
        var chunkHints = vectorHits.ToDictionary(h => h.PageId, h => h.ChunkIndex);

        if (vectorHits.Count == 0)
        {
            return (keywordCandidates, chunkHints);
        }

        var scores = new Dictionary<Guid, double>();
        var keywordRank = new Dictionary<Guid, int>();
        for (var i = 0; i < keywordCandidates.Count; i++)
        {
            keywordRank[keywordCandidates[i].PageId] = i + 1;
            scores[keywordCandidates[i].PageId] = 1.0 / (RrfK + i + 1);
        }

        for (var i = 0; i < vectorHits.Count; i++)
        {
            scores[vectorHits[i].PageId] =
                scores.GetValueOrDefault(vectorHits[i].PageId) + 1.0 / (RrfK + i + 1);
        }

        var candidatesById = keywordCandidates.ToDictionary(c => c.PageId);
        var missingIds = scores.Keys.Where(id => !candidatesById.ContainsKey(id)).ToList();
        if (missingIds.Count > 0)
        {
            var rows = await _db.Pages
                .Where(p => missingIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Title, p.SpaceId, SpaceKey = p.Space!.Key, p.CurrentContent, p.AncestorPath })
                .ToListAsync(cancellationToken);
            foreach (var r in rows)
            {
                candidatesById[r.Id] = new SearchCandidate(r.Id, r.Title, r.SpaceId, r.SpaceKey, r.CurrentContent, r.AncestorPath);
            }
        }

        var fused = scores
            .OrderByDescending(kv => kv.Value)
            // Deterministic ties: keyword order first (it already encodes rank/recency), then id.
            .ThenBy(kv => keywordRank.GetValueOrDefault(kv.Key, int.MaxValue))
            .ThenBy(kv => kv.Key)
            .Select(kv => candidatesById.GetValueOrDefault(kv.Key))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        return (fused, chunkHints);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return double.MinValue; // incomparable - never wins
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? double.MinValue : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
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
    /// design.md §9.3: over-fetch, then filter by canView, preserving the fused
    /// rank/recency order - stops as soon as maxResults visible hits
    /// are found rather than scoring every candidate.
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> FilterByCanViewAsync(
        List<SearchCandidate> candidates, Dictionary<Guid, int> chunkHints, string query,
        Principal principal, int maxResults, CancellationToken cancellationToken)
    {
        // Two queries for the whole candidate set regardless of its size; the per-hit
        // check below is pure in-memory rule evaluation.
        var batch = await _permissions.LoadBatchAsync(candidates.Select(Subject).ToList(), cancellationToken);

        var hits = new List<SearchHit>();
        foreach (var candidate in candidates)
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            // Replica status is irrelevant to canView (design.md §6.4).
            var permission = batch.For(Subject(candidate), isReplicaSpace: false).Compute(principal);
            if (permission.CanView)
            {
                // Snippet/heading/anchor are computed HERE, strictly after canView passed
                // - restricted content never reaches the excerpting code at all
                // (design.md §6.7: a hidden page must not leak via title, snippet, or count).
                hits.Add(BuildHit(candidate, query,
                    chunkHints.TryGetValue(candidate.PageId, out var chunkIndex) ? chunkIndex : null));
            }
        }

        return hits;
    }

    /// <summary>
    /// Attributes the hit to a section. Keyword-locatable matches keep milestone 4's
    /// behavior: the section containing the first literal term match, heading breadcrumb
    /// (design.md §9's deep-link contract) + anchor id via the ported cross-language
    /// algorithm, and a plain-text excerpt centered on the match. A hit the literal scan
    /// can't locate but the vector index found (semantic-only - the whole point of §9.2)
    /// is attributed to its best-matching CHUNK instead: the page's chunks are recomputed
    /// from CurrentContent - deliberately not read back from the vector store, so
    /// everything user-visible derives from content canView just passed, and a stale
    /// index between an edit and its re-embed can at worst mis-aim a deep link, never
    /// resurface old text. No literal match, no chunk hint (title-only / FTS-stem hits)
    /// degrades to a leading excerpt with no section attribution - correct, just less
    /// specific.
    /// </summary>
    private static SearchHit BuildHit(SearchCandidate candidate, string query, int? semanticChunkIndex)
    {
        var content = candidate.CurrentContent;
        var matchIndex = SnippetBuilder.LocateFirstMatch(content, query, out var matchLength);

        if (matchIndex < 0 && semanticChunkIndex is int chunkIndex)
        {
            var chunks = MarkdownChunker.Chunk(content);
            if (chunkIndex >= 0 && chunkIndex < chunks.Count)
            {
                var chunk = chunks[chunkIndex];
                return new SearchHit(
                    candidate.PageId,
                    candidate.Title,
                    candidate.SpaceKey,
                    SnippetBuilder.Build(chunk.Text, 0, 0, SnippetLength),
                    chunk.HeadingPath,
                    chunk.AnchorId);
            }
        }

        var snippet = matchIndex >= 0
            ? SnippetBuilder.Build(content, matchIndex, matchLength, SnippetLength)
            : SnippetBuilder.Build(content, 0, 0, SnippetLength);

        IReadOnlyList<string> headingPath = [];
        var anchorId = string.Empty;

        if (matchIndex >= 0)
        {
            var headings = MarkdownHeadings.Extract(content);
            var sectionIndex = -1;
            for (var i = 0; i < headings.Count && headings[i].Offset <= matchIndex; i++)
            {
                sectionIndex = i;
            }

            if (sectionIndex >= 0)
            {
                var infos = headings.Select(h => new HeadingInfo(h.Level, h.Text)).ToArray();
                headingPath = HeadingAnchors.ComputeHeadingPaths(infos)[sectionIndex];
                anchorId = HeadingAnchors.ComputeHeadingAnchors(infos)[sectionIndex];
            }
        }

        return new SearchHit(candidate.PageId, candidate.Title, candidate.SpaceKey, snippet, headingPath, anchorId);
    }

    /// <summary>A candidate row's authorization key — the search projection never
    /// materializes a Page entity, so the chain comes from its AncestorPath.</summary>
    private static PermissionSubject Subject(SearchCandidate candidate) =>
        new(candidate.PageId, candidate.SpaceId, candidate.AncestorPath);

    private sealed record SearchCandidate(Guid PageId, string Title, Guid SpaceId, string SpaceKey, string CurrentContent, string AncestorPath);

    private sealed record VectorHit(Guid PageId, int ChunkIndex, double Similarity);

    private sealed class VectorDistanceRow
    {
        public Guid PageId { get; init; }
        public int ChunkIndex { get; init; }
        public double Distance { get; init; }
    }

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
