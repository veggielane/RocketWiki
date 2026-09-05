using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using RocketWiki.Api.Ai;
using RocketWiki.Api.Features;

namespace RocketWiki.Api.Assistant;

/// <summary>
/// Wires the "ask the wiki" assistant (design.md §9, resolving §17's assistant
/// bullet): the OpenAI-compatible <see cref="IChatClient"/> and its options,
/// mirroring <see cref="Embeddings.EmbeddingPipelineConfiguration"/> deliberately —
/// the chat endpoint is the same class of in-network, §9.4-boundary dependency as
/// the embedding endpoint, configured the same two ways:
///
/// 1. The Aspire-injected <c>assistant</c> connection string
///    (<c>Endpoint=…;Key=…;Model=…</c>, bare URL accepted as Endpoint-only) — the
///    AppHost's <c>AddConnectionString("assistant")</c>, §15 "config by reference".
/// 2. The <c>Ai</c> section (<see cref="AiOptions"/>) for anything the connection string
///    doesn't carry: <c>Ai:ChatModel</c> names the model, and the endpoint/key fall back
///    to §9.2's shared <c>Ai:BaseUrl</c>/<c>Ai:ApiKey</c> — one gateway serving both the
///    embedding and chat models is the expected deployment, so the second model
///    should be one config key, not a duplicated section.
///
/// <b>Configured is opt-in; absent is a supported state (§15 fail-closed).</b> When
/// no endpoint/model resolves, no client and no options register: AskWikiService
/// (always registered — the schema must not change shape with configuration, the
/// GitLab precedent) sees nulls and answers NOT_CONFIGURED without touching
/// retrieval or the network. There is no default endpoint: the question and
/// viewable page content travel to this URL, so guessing one would be a
/// content-exfiltration bug, not a convenience.
///
/// <b>The <c>AskWiki</c> feature flag is a second, independent way to be absent</b>
/// (docs/CONFIGURATION.md "Feature flags"): flag off produces exactly the unconfigured
/// container — no options, no client — regardless of what is configured, so a demo can
/// silence the assistant without deleting its connection string. The flag is checked
/// FIRST and the configuration second; a flag alone registers nothing, because there is
/// nothing for it to point a client at. Same schema either way; the SPA reads
/// <c>assistantStatus.configured</c> and hides the surface.
///
/// Resilience: one attempt, bounded timeout, no retries — the GitLab reasoning
/// (a user-facing request retrying against a down endpoint at exactly the wrong
/// moment), enforced here via the OpenAI client's own pipeline options rather than
/// an IHttpClientFactory handler chain, because System.ClientModel clients own
/// their transport and never pass through the factory's resilience defaults.
///
/// Telemetry (§15, decided rather than cargo-culted from the GitLab handler
/// surgery): this client's built-in HTTP span is NOT suppressed. GitLab's was
/// because its URL path carries repository file paths — content by another name.
/// Here <c>url.full</c> is config-static (<c>{BaseUrl}/chat/completions</c>): it
/// names the operator's own configured endpoint and nothing about any question or
/// page. The sensitive part of an ask is the request BODY, which no built-in
/// HttpClient/ClientModel instrumentation records anywhere. The factory's
/// full-URI request logging doesn't apply either — this client never goes through
/// IHttpClientFactory. Both facts are what AssistantTelemetryHygieneTests sweeps
/// for. This exactly matches the embeddings client's posture, established for the
/// same endpoint class.
/// </summary>
public static class AssistantConfiguration
{
    public static void AddRocketWikiAssistant(this WebApplicationBuilder builder, FeatureFlagSnapshot features)
    {
        // Always registered, configured or not — the resolver answers NOT_CONFIGURED
        // through this service's null options/client (optional ctor parameters, the
        // same DI pattern SearchService uses for the optional embedding generator).
        builder.Services.AddScoped<AskWikiService>();

        if (!features.AskWiki)
        {
            return; // Flag off: the unconfigured shape, whatever is configured. See class doc.
        }

        // Connection string first, Ai section as per-value fallback — the shared rule
        // (AiConnectionStringParser); the keys this feature reads are the class doc's
        // list: Ai:BaseUrl / Ai:ApiKey (shared with the embedding endpoint, since one
        // gateway serving both models is the expected deployment) and Ai:ChatModel.
        //
        // The ENDPOINT keys are read EAGERLY off the builder, unlike GitLabConfiguration
        // which deliberately resolves its options from the container instead. The
        // difference is real and worth stating rather than leaving as an apparent
        // inconsistency: GitLab must register its resolvers either way (the schema
        // cannot change shape with configuration), so it needs a value that can still be
        // decided after WebApplicationFactory layers test configuration in during
        // Build(). Here the configuration decides whether to register AT ALL, which has
        // to happen before Build() by definition. The test tier reaches this path with
        // UseSetting (which travels as a command-line argument and is visible here —
        // FeatureFlagOffStateTests), and AskWikiApiFixture bypasses it by injecting
        // IChatClient and AssistantOptions straight into the container, because "is it
        // configured" is not what those tests are about.
        //
        // The TUNING keys (timeout, caps) are not needed to decide anything, so they are
        // resolved lazily from IOptions<AiOptions> in the factories below — the one
        // registration AiConfiguration binds and validates — not captured here.
        var ai = AiOptions.BindEagerly(builder.Configuration);
        var (endpoint, key, model, _) = AiConnectionStringParser.Resolve(
            builder.Configuration, connectionName: "assistant", ai, o => o.ChatModel);
        if (endpoint is null || model is null)
        {
            return; // Not configured: feature absent, fail closed. See class doc.
        }

        builder.Services.AddSingleton(sp =>
        {
            var tuning = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return new AssistantOptions(
                ChatModel: model,
                Timeout: tuning.ChatTimeout,
                MaxContextChars: tuning.MaxContextChars,
                MaxRetrievedPages: tuning.MaxRetrievedPages,
                MaxQuestionChars: tuning.MaxQuestionChars,
                MaxOutputTokens: tuning.MaxOutputTokens);
        });

        // Same construction as the embedding generator (§9.2): official OpenAI 2.x
        // client at the configured in-boundary endpoint, surfaced through
        // Microsoft.Extensions.AI so the provider stays pure config. Keyless
        // gateways get the same "unused" placeholder.
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<AssistantOptions>();
            return new OpenAIClient(
                    new ApiKeyCredential(string.IsNullOrEmpty(key) ? "unused" : key),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri(endpoint),
                        NetworkTimeout = options.Timeout,
                        // No retries — see class doc. maxRetries: 0 keeps the policy's
                        // bookkeeping but never re-sends.
                        RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                    })
                .GetChatClient(options.ChatModel)
                .AsIChatClient();
        });
    }
}
