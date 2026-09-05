using System.Data.Common;

namespace RocketWiki.Api.Ai;

/// <summary>
/// The config-resolution rule both OpenAI-compatible endpoints follow (design.md
/// §9.2/§9.5, §15 "config by reference"): the Aspire-injected connection string first,
/// then the shared <c>Ai:</c> section per value — supplied here as an eagerly bound
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
    /// <param name="model">The per-feature model fallback (<c>EmbeddingModel</c>,
    /// <c>ChatModel</c>) — the one fallback that differs.</param>
    internal static AiEndpointSettings Resolve(
        IConfiguration configuration, string connectionName, AiOptions fallback, Func<AiOptions, string?> model)
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
        }

        endpoint ??= NullIfEmpty(fallback.BaseUrl);
        key ??= NullIfEmpty(fallback.ApiKey);
        modelName ??= NullIfEmpty(model(fallback));

        return new AiEndpointSettings(endpoint, key, modelName, dimensions);
    }

    private static string? ValueOrNull(DbConnectionStringBuilder builder, string keyword) =>
        builder.TryGetValue(keyword, out var value) && value is string s && s.Length > 0 ? s : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
