using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Search;
using RocketWiki.Data;
using RocketWiki.Data.Configurations;

namespace RocketWiki.Api.Embeddings;

/// <summary>
/// data-model.md / design.md §9.3: on SQL Server, PageEmbeddings.Embedding is a native
/// vector(1536) column — dimensions are FIXED PER COLUMN by the schema, where the old
/// varbinary blob would silently store any length. A configured <c>Ai:Dimensions</c>
/// (or embeddings connection-string <c>Dimensions=…</c>) that disagrees with the column
/// can never work: every indexer write and every VECTOR_DISTANCE query would be
/// rejected by the engine. So refuse to start at all — a loud, immediate startup
/// failure naming the fix — rather than boot into a host whose embedding pipeline
/// fails on every page and whose search errors on every semantic query.
///
/// Changing the embedding model to one with different dimensions is therefore a
/// migration (alter the column + this constant) plus a full re-embed, exactly as
/// data-model.md promises — never a config edit.
///
/// SQL Server only, checked at runtime against the actual provider: on SQLite (the
/// integration-test tier) the blob column fixes nothing and tests legitimately run
/// tiny vectors, so this check structurally does not apply there. Registered by
/// <see cref="EmbeddingPipelineConfiguration"/> before the background service, and only
/// when embeddings are configured at all — an unconfigured host has nothing to check.
/// </summary>
public sealed class EmbeddingDimensionsStartupCheck : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmbeddingOptions _options;

    public EmbeddingDimensionsStartupCheck(IServiceScopeFactory scopeFactory, EmbeddingOptions options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer"
            && _options.Dimensions != PageEmbeddingConfiguration.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding dimensions mismatch: configuration requests {_options.Dimensions}-dimensional " +
                $"vectors but the PageEmbeddings.Embedding column is vector({PageEmbeddingConfiguration.EmbeddingDimensions}) " +
                "— dimensions are fixed per column on SQL Server (data-model.md). Changing the embedding " +
                "model or its dimensions requires a schema migration (see PageEmbeddingConfiguration." +
                "EmbeddingDimensions and the AlterPageEmbeddingToNativeVector migration) and a full " +
                "re-embed; it cannot be done via configuration.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
