namespace RocketWiki.Api.Ai;

/// <summary>
/// Binds and validates the <c>Ai</c> section (<see cref="AiOptions"/>) — the shared half
/// of the two AI features' configuration, registered once and ahead of both so that
/// <c>AddRocketWikiEmbeddings</c> and <c>AddRocketWikiAssistant</c> resolve
/// <c>IOptions&lt;AiOptions&gt;</c> from one registration rather than each binding the
/// section again. The same <c>AddOptions().Bind().ValidateDataAnnotations().ValidateOnStart()</c>
/// chain as the upload caps and the co-editing caps (Program.cs): an explicit bad value —
/// a zero batch size, a negative timeout, a zero question cap — fails the host at boot
/// with the offending key named, whether or not either endpoint is configured. A tuning
/// key on an instance with no AI endpoint is still configuration, and a wrong one is
/// still a mistake worth surfacing where an operator is looking.
///
/// <para>The per-feature sections (<c>Ai:Embeddings:*</c>, <c>Ai:Assistant:*</c>) are
/// validated explicitly below: DataAnnotations does not descend into a nested object, so
/// an attribute on <c>AiEmbeddingsEndpointOptions.Dimensions</c> would never run — the
/// same reason <c>FileStorageOptions</c> validates its nested sections by hand. The
/// endpoint URLs are checked for shape at every layer here, so a scheme-less
/// <c>host:port</c> is refused at boot with the key named rather than surfacing as a
/// <c>UriFormatException</c> on the first embedding or question.</para>
/// </summary>
public static class AiConfiguration
{
    public static WebApplicationBuilder AddRocketWikiAi(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AiOptions>()
            .Bind(builder.Configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Embeddings.Dimensions is null or > 0,
                "Ai:Embeddings:Dimensions must be a positive vector width.")
            .Validate(o => AiOptions.IsAbsoluteHttpUrlOrUnset(o.BaseUrl),
                "Ai:BaseUrl must be an absolute http:// or https:// URL.")
            .Validate(o => AiOptions.IsAbsoluteHttpUrlOrUnset(o.Embeddings.Endpoint),
                "Ai:Embeddings:Endpoint must be an absolute http:// or https:// URL.")
            .Validate(o => AiOptions.IsAbsoluteHttpUrlOrUnset(o.Assistant.Endpoint),
                "Ai:Assistant:Endpoint must be an absolute http:// or https:// URL.")
            .ValidateOnStart();

        return builder;
    }
}
