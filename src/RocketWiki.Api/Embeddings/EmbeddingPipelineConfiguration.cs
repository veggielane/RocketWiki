using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using RocketWiki.Api.Ai;
using RocketWiki.Core.Search;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.Embeddings;

/// <summary>
/// design.md §9.2/§9.3 (milestone 7): wires the embedding pipeline — the
/// OpenAI-compatible <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, the
/// <see cref="EmbeddingIndexer"/>, and the polling <see cref="EmbeddingBackgroundService"/>.
///
/// <b>Configured is opt-in; absent is a supported state.</b> When no endpoint/model is
/// configured, nothing registers: SearchService sees a null generator and serves
/// keyword-only results, no background job runs, and page saves are untouched — the
/// §9.2 degradation, wired structurally rather than checked per request. That is also
/// why the integration-test factory needs no special casing.
///
/// Config sources, in precedence order per value:
/// 1. The Aspire-injected <c>embeddings</c> connection string (design.md §15 "config by
///    reference": the AppHost's <c>AddConnectionString("embeddings")</c>), in the usual
///    .NET AI connection-string shape <c>Endpoint=…;Key=…;Model=…;Dimensions=…</c>
///    (a bare URL is accepted as Endpoint-only).
/// 2. The <c>Ai</c> section (§9.2's documented shape: BaseUrl / ApiKey / EmbeddingModel /
///    Dimensions) for anything the connection string doesn't carry — and the whole story
///    when running standalone outside Aspire.
///
/// §9.4 is a deployment requirement this code can't enforce but its shape supports: page
/// content travels to this endpoint, so it must live inside the network's security
/// boundary — self-hosted or an approved gateway, never a public API.
/// </summary>
public static class EmbeddingPipelineConfiguration
{
    public static void AddRocketWikiEmbeddings(this WebApplicationBuilder builder)
    {
        // Connection string first, Ai section as per-value fallback — the shared rule
        // (AiConnectionStringParser); the keys this feature reads are the class doc's
        // list: Ai:BaseUrl / Ai:ApiKey / Ai:EmbeddingModel / Ai:Dimensions.
        var (endpoint, key, model, dimensions) = AiConnectionStringParser.Resolve(
            builder.Configuration, connectionName: "embeddings", modelConfigKey: "Ai:EmbeddingModel");

        if (endpoint is null || model is null)
        {
            return; // Not configured: keyword-only search, no job. Deliberate, see class doc.
        }

        var options = new EmbeddingOptions(
            Model: model,
            Dimensions: dimensions ?? 1536,
            PollInterval: SecondsOrNull(builder.Configuration, "Ai:PollSeconds"),
            BatchSize: builder.Configuration.GetValue("Ai:BatchSize", 16),
            FailureBackoff: SecondsOrNull(builder.Configuration, "Ai:FailureBackoffSeconds"));

        builder.Services.AddSingleton(options);

        // The official OpenAI 2.x client pointed at the configured (self-hosted /
        // gateway) endpoint, surfaced through Microsoft.Extensions.AI's abstraction so
        // the concrete provider stays pure config (§9.2). Many in-boundary gateways are
        // keyless; ApiKeyCredential requires a non-empty value, so absence becomes a
        // placeholder the gateway ignores.
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
            new OpenAIClient(
                    new ApiKeyCredential(string.IsNullOrEmpty(key) ? "unused" : key),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetEmbeddingClient(options.Model)
                .AsIEmbeddingGenerator(options.Dimensions));

        builder.Services.AddScoped<EmbeddingIndexer>();
        // Registered BEFORE the background service (hosted services start in
        // registration order): a Dimensions setting that contradicts the SQL Server
        // vector(1536) column fails startup before the job can attempt a single
        // doomed write. See EmbeddingDimensionsStartupCheck.
        builder.Services.AddHostedService<EmbeddingDimensionsStartupCheck>();
        builder.Services.AddHostedService<EmbeddingBackgroundService>();
    }

    private static TimeSpan? SecondsOrNull(IConfiguration configuration, string configKey)
    {
        var seconds = configuration.GetValue<int?>(configKey);
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
    }
}
