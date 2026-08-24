using System.Data.Common;

namespace RocketWiki.Api.Ai;

/// <summary>
/// The config-resolution rule both OpenAI-compatible endpoints follow (design.md
/// §9.2/§9.5, §15 "config by reference"): the Aspire-injected connection string first,
/// then the shared <c>Ai:</c> section per value.
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
/// </summary>
internal static class AiConnectionStringParser
{
    /// <summary><see cref="Dimensions"/> is only meaningful to the embedding caller; the
    /// chat caller ignores it (no chat endpoint declares one), which costs one unread
    /// int and keeps a single parser.</summary>
    internal readonly record struct AiEndpointSettings(string? Endpoint, string? Key, string? Model, int? Dimensions);

    /// <param name="connectionName">Connection-string name the AppHost injects
    /// (<c>embeddings</c>, <c>assistant</c>). Shape:
    /// <c>Endpoint=…;Key=…;Model=…;Dimensions=…</c>; a value with no <c>=</c> is taken
    /// as a bare endpoint URL.</param>
    /// <param name="modelConfigKey">The per-feature model key in the <c>Ai</c> section
    /// (<c>Ai:EmbeddingModel</c>, <c>Ai:ChatModel</c>) — the one fallback that differs,
    /// because one gateway commonly serves both models.</param>
    internal static AiEndpointSettings Resolve(
        IConfiguration configuration, string connectionName, string modelConfigKey)
    {
        string? endpoint = null, key = null, model = null;
        int? dimensions = null;

        var connectionString = configuration.GetConnectionString(connectionName);
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
        model ??= configuration[modelConfigKey];
        dimensions ??= configuration.GetValue<int?>("Ai:Dimensions");

        return new AiEndpointSettings(endpoint, key, model, dimensions);
    }

    private static string? ValueOrNull(DbConnectionStringBuilder builder, string keyword) =>
        builder.TryGetValue(keyword, out var value) && value is string s && s.Length > 0 ? s : null;
}
