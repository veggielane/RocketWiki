using System.ClientModel;
using System.Data.Common;
using Microsoft.Extensions.AI;
using OpenAI;
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
        var (endpoint, key, model, dimensions) = ResolveConfiguration(builder.Configuration);

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

    private static (string? Endpoint, string? Key, string? Model, int? Dimensions) ResolveConfiguration(
        IConfiguration configuration)
    {
        string? endpoint = null, key = null, model = null;
        int? dimensions = null;

        var connectionString = configuration.GetConnectionString("embeddings");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (connectionString.Contains('=', StringComparison.Ordinal))
            {
                var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
                endpoint = ValueOrNull(csb, "Endpoint");
                key = ValueOrNull(csb, "Key");
                model = ValueOrNull(csb, "Model");
                dimensions = int.TryParse(ValueOrNull(csb, "Dimensions"), out var d) ? d : null;
            }
            else
            {
                endpoint = connectionString.Trim(); // bare URL
            }
        }

        endpoint ??= configuration["Ai:BaseUrl"];
        key ??= configuration["Ai:ApiKey"];
        model ??= configuration["Ai:EmbeddingModel"];
        dimensions ??= configuration.GetValue<int?>("Ai:Dimensions");

        return (endpoint, key, model, dimensions);
    }

    private static string? ValueOrNull(DbConnectionStringBuilder builder, string keyword) =>
        builder.TryGetValue(keyword, out var value) && value is string s && s.Length > 0 ? s : null;

    private static TimeSpan? SecondsOrNull(IConfiguration configuration, string configKey)
    {
        var seconds = configuration.GetValue<int?>(configKey);
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
    }
}
