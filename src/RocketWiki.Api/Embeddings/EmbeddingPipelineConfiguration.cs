using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using RocketWiki.Api.Ai;
using RocketWiki.Api.Features;
using RocketWiki.Core.Search;
using RocketWiki.Data.Configurations;
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
/// <b>The <c>SemanticSearch</c> feature flag is a second, independent way to be absent</b>
/// (docs/CONFIGURATION.md "Feature flags"): flag off registers exactly what unconfigured
/// registers — nothing — regardless of the endpoint, so no page content leaves for it,
/// and search is keyword-only. Checked first, configuration second; a flag alone
/// registers nothing. Note that "ask the wiki" retrieves through the same search, so with
/// this flag off its retrieval is keyword-only too.
///
/// Config sources, in precedence order per value:
/// 1. The Aspire-injected <c>embeddings</c> connection string (design.md §15 "config by
///    reference": the AppHost's <c>AddConnectionString("embeddings")</c>), in the usual
///    .NET AI connection-string shape <c>Endpoint=…;Key=…;Model=…;Dimensions=…</c>
///    (a bare URL is accepted as Endpoint-only).
/// 2. The <c>Ai</c> section (<see cref="AiOptions"/>; §9.2's documented shape: BaseUrl /
///    ApiKey / EmbeddingModel / Dimensions) for anything the connection string doesn't
///    carry — and the whole story when running standalone outside Aspire.
///
/// The endpoint keys are read eagerly (they decide whether anything registers, which
/// happens before <c>Build()</c>); the job's tuning — dimensions fallback, poll interval,
/// batch size, backoff, attempt ceiling, timeout — is resolved lazily from
/// <c>IOptions&lt;AiOptions&gt;</c> when <see cref="EmbeddingOptions"/> is first needed,
/// so it is validated once (AiConfiguration) and read from the configuration in force.
///
/// §9.4 is a deployment requirement this code can't enforce but its shape supports: page
/// content travels to this endpoint, so it must live inside the network's security
/// boundary — self-hosted or an approved gateway, never a public API.
/// </summary>
public static class EmbeddingPipelineConfiguration
{
    public static void AddRocketWikiEmbeddings(this WebApplicationBuilder builder, FeatureFlagSnapshot features)
    {
        if (!features.SemanticSearch)
        {
            return; // Flag off: keyword-only search, no job — the unconfigured shape. See class doc.
        }

        // Connection string first, Ai section as per-value fallback — the shared rule
        // (AiConnectionStringParser); the keys this feature reads are the class doc's
        // list: Ai:BaseUrl / Ai:ApiKey / Ai:EmbeddingModel (eager) and Ai:Dimensions (lazy).
        var ai = AiOptions.BindEagerly(builder.Configuration);
        var (endpoint, key, model, connectionStringDimensions) = AiConnectionStringParser.Resolve(
            builder.Configuration, connectionName: "embeddings", ai, o => o.EmbeddingModel);

        if (endpoint is null || model is null)
        {
            return; // Not configured: keyword-only search, no job. Deliberate, see class doc.
        }

        builder.Services.AddSingleton(sp =>
        {
            var tuning = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return new EmbeddingOptions(
                Model: model,
                // Connection string first, then Ai:Dimensions, then the column's fixed
                // width (data-model.md) — the same value EmbeddingDimensionsStartupCheck
                // compares against on SQL Server.
                Dimensions: connectionStringDimensions ?? tuning.Dimensions ?? PageEmbeddingConfiguration.EmbeddingDimensions,
                PollInterval: tuning.PollInterval,
                BatchSize: tuning.BatchSize,
                FailureBackoff: tuning.FailureBackoff,
                MaxAttempts: tuning.MaxAttempts,
                RequestTimeout: tuning.EmbeddingTimeout);
        });

        // The official OpenAI 2.x client pointed at the configured (self-hosted /
        // gateway) endpoint, surfaced through Microsoft.Extensions.AI's abstraction so
        // the concrete provider stays pure config (§9.2). Many in-boundary gateways are
        // keyless; ApiKeyCredential requires a non-empty value, so absence becomes a
        // placeholder the gateway ignores.
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<EmbeddingOptions>();
            return new OpenAIClient(
                    new ApiKeyCredential(string.IsNullOrEmpty(key) ? "unused" : key),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri(endpoint),
                        // The same posture as the assistant client (§9.5), which
                        // documents itself as matching THIS one — it did not: this
                        // client set neither, so a hung endpoint stalled a poll
                        // iteration indefinitely and SDK-default retries ran
                        // underneath the job's own 5-minute backoff, multiplying
                        // every failure invisibly.
                        NetworkTimeout = options.RequestTimeoutOrDefault,
                        // No SDK retries: retrying is the JOB's decision, and it
                        // already has one (backoff, attempt ceiling, next poll).
                        // A second retry policy underneath that one is invisible to
                        // the attempt count an operator reads.
                        RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                    })
                .GetEmbeddingClient(options.Model)
                .AsIEmbeddingGenerator(options.Dimensions);
        });

        builder.Services.AddScoped<EmbeddingIndexer>();
        // Registered BEFORE the background service (hosted services start in
        // registration order): a Dimensions setting that contradicts the SQL Server
        // vector(1536) column fails startup before the job can attempt a single
        // doomed write. See EmbeddingDimensionsStartupCheck.
        builder.Services.AddHostedService<EmbeddingDimensionsStartupCheck>();
        builder.Services.AddHostedService<EmbeddingBackgroundService>();
    }
}
