namespace RocketWiki.Core.Search;

/// <summary>
/// The embedding pipeline's settled configuration (design.md §9.2), resolved once at
/// startup from the Aspire-injected <c>embeddings</c> connection string and the
/// <c>Ai</c> section (see RocketWiki.Api's embedding wiring for precedence). Registered
/// in DI only when an endpoint and model are actually configured — its absence is how
/// the search service and background job know to degrade to keyword-only (§9.2: while
/// no endpoint exists or the endpoint is down, search degrades gracefully to FTS-only).
///
/// Lives in Core (plain record, no AI package dependency) because both RocketWiki.Data
/// (indexer, hybrid search) and RocketWiki.Api (wiring, background job) need it.
/// </summary>
/// <param name="Model">Stamped on every <c>PageEmbedding</c> row and checked at query
/// time — an index only ever contains one model's vectors (§9.3). Changing the model
/// means re-embedding everything.</param>
/// <param name="Dimensions">Requested of the endpoint and validated on every response;
/// fixed per index (data-model.md).</param>
/// <param name="PollInterval">How often the background job scans for pages whose
/// current revision differs from their embedded revision.</param>
/// <param name="BatchSize">Maximum pages (re-)embedded per job run — bounds a single
/// run's work; the next run picks up where this one stopped.</param>
/// <param name="FailureBackoff">How long a page with a failed attempt waits before
/// being retried (§9.2: failures retry; saves are never blocked).</param>
public sealed record EmbeddingOptions(
    string Model,
    int Dimensions = 1536,
    TimeSpan? PollInterval = null,
    int BatchSize = 16,
    TimeSpan? FailureBackoff = null)
{
    public TimeSpan PollIntervalOrDefault => PollInterval ?? TimeSpan.FromSeconds(30);

    public TimeSpan FailureBackoffOrDefault => FailureBackoff ?? TimeSpan.FromMinutes(5);
}
