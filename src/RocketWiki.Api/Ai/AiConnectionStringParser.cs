using System.Data.Common;

namespace RocketWiki.Api.Ai;

/// <summary>
/// The config-resolution rule both OpenAI-compatible endpoints follow (design.md
/// §9.2/§9.5, §15 "config by reference"): the Aspire-injected connection string first,
/// then the feature's own <c>Ai:Embeddings:*</c> / <c>Ai:Assistant:*</c> section, then
/// the shared <c>Ai:</c> fallbacks — per value, all supplied here as an eagerly bound
/// <see cref="AiOptions"/>, the one definition of that section's shape.
///
/// One implementation, because the two callers had it line for line identical and the
/// precedence is a §15 decision rather than a per-feature choice: an operator who learns
/// how the embedding endpoint is configured has learned how the chat endpoint is
/// configured. Each caller documents which keys and fallbacks *it* uses — that part is
/// genuinely per-feature, and stays with the feature.
///
/// Neither endpoint has a default: page content and questions travel to these URLs, so
/// an unresolved value must mean "the feature is absent", never a guess (§9.4's boundary
/// requirement, the same fail-closed posture as <c>GitLab:BaseUrl</c>).
///
/// The connection string itself stays on <c>GetConnectionString</c> — Aspire injects it
/// and that is the idiomatic API; it is not wrapped in an options class.
/// </summary>
internal static class AiConnectionStringParser
{
    /// <summary><see cref="Dimensions"/> is the connection string's <c>Dimensions=</c>
    /// only — a tuning value, so its <c>Ai:Dimensions</c> fallback is applied lazily by the
    /// embedding caller from <c>IOptions&lt;AiOptions&gt;</c> rather than here. The chat
    /// caller ignores it (no chat endpoint declares one), which costs one unread int and
    /// keeps a single parser.</summary>
    internal readonly record struct AiEndpointSettings(string? Endpoint, string? Key, string? Model, int? Dimensions);

    /// <param name="connectionName">Connection-string name the AppHost injects
    /// (<c>embeddings</c>, <c>assistant</c>). Shape:
    /// <c>Endpoint=…;Key=…;Model=…;Dimensions=…</c>; a value with no <c>=</c> is taken
    /// as a bare endpoint URL.</param>
    /// <param name="fallback">The <c>Ai</c> section, bound eagerly (<see cref="AiOptions.BindEagerly"/>):
    /// <c>BaseUrl</c>/<c>ApiKey</c> are shared by both endpoints because one gateway
    /// commonly serves both models.</param>
    /// <param name="feature">The feature's own section (<see cref="AiOptions.Embeddings"/>
    /// or <see cref="AiOptions.Assistant"/>), consulted per value between the connection
    /// string and the shared fallbacks.</param>
    /// <param name="sharedModel">The older per-feature model key on the shared section
    /// (<c>EmbeddingModel</c>, <c>ChatModel</c>), the last resort for the model name.</param>
    /// <exception cref="InvalidOperationException">The connection string names an endpoint
    /// that is not an absolute http(s) URL. Thrown here, at registration, because the
    /// connection string is outside the validated options family: without this the first
    /// embedding or question would die of a <c>UriFormatException</c> instead of the host
    /// refusing to boot with the value named. Endpoints from the <c>Ai:*</c> keys are
    /// checked by <see cref="AiConfiguration"/> instead, so every bad key is reported
    /// together at startup rather than the first one aborting the rest.</exception>
    internal static AiEndpointSettings Resolve(
        IConfiguration configuration,
        string connectionName,
        AiOptions fallback,
        AiEndpointOptions feature,
        Func<AiOptions, string?> sharedModel)
    {
        string? endpoint = null, key = null, modelName = null;
        int? dimensions = null;

        var connectionString = configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (connectionString.Contains('=', StringComparison.Ordinal))
            {
                var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
                endpoint = ValueOrNull(csb, "Endpoint");
                key = ValueOrNull(csb, "Key");
                modelName = ValueOrNull(csb, "Model");
                dimensions = int.TryParse(ValueOrNull(csb, "Dimensions"), out var d) ? d : null;
            }
            else
            {
                endpoint = connectionString.Trim(); // bare URL
            }

            if (!AiOptions.IsAbsoluteHttpUrlOrUnset(endpoint))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{connectionName}: Endpoint must be an absolute http:// or https:// URL, got '{endpoint}'.");
            }
        }

        endpoint ??= NullIfEmpty(feature.Endpoint) ?? NullIfEmpty(fallback.BaseUrl);
        key ??= NullIfEmpty(feature.ApiKey) ?? NullIfEmpty(fallback.ApiKey);
        modelName ??= NullIfEmpty(feature.Model) ?? NullIfEmpty(sharedModel(fallback));

        return new AiEndpointSettings(endpoint, key, modelName, dimensions);
    }

    private static string? ValueOrNull(DbConnectionStringBuilder builder, string keyword) =>
        builder.TryGetValue(keyword, out var value) && value is string s && s.Length > 0 ? s : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
