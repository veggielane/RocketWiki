using RocketWiki.Core.Search;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.Embeddings;

/// <summary>
/// design.md §9.2: "embedding runs as a background job … never blocking saves". The
/// polling half of the pipeline: every <see cref="EmbeddingOptions.PollIntervalOrDefault"/>
/// it opens a fresh scope and lets <see cref="EmbeddingIndexer"/> scan for pages whose
/// current revision isn't embedded yet (why a scan and not an event subscription:
/// see PageEmbeddingState — it also covers the sync CLI's out-of-process imports and is
/// restart-safe).
///
/// Failure isolation is absolute here: nothing thrown by a run may escape this loop —
/// an unreachable endpoint is logged (exception type only, §15) and the next tick
/// retries, while search meanwhile degrades to keyword-only. Registered only when the
/// embedding endpoint is configured (EmbeddingPipelineConfiguration).
///
/// System actor, not a user: index runs write no user audit events (§9.2) and raise no
/// domain events, so they neither need an AuditContext nor touch the sync outbox.
/// </summary>
public sealed class EmbeddingBackgroundService(
    IServiceScopeFactory scopeFactory,
    EmbeddingOptions options,
    ILogger<EmbeddingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollIntervalOrDefault);

        try
        {
            // First tick arrives one interval after startup — deliberately: startup work
            // (migrations, seeding) finishes before the first scan competes for the pool.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var indexer = scope.ServiceProvider.GetRequiredService<EmbeddingIndexer>();
                    await indexer.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // §15: type only, never a message that could echo request content.
                    // The exception OBJECT is deliberately not passed: ILogger renders it
                    // with ToString(), i.e. message + stack, and the OpenAI client builds
                    // its message from the endpoint's response body — which commonly
                    // echoes the rejected input back. That is page content, in a log, on
                    // the one emission channel the §15 hygiene test cannot intercept.
                    logger.LogError("Embedding index run failed ({ExceptionType}); next poll retries", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
