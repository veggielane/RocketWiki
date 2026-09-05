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
/// </summary>
public static class AiConfiguration
{
    public static WebApplicationBuilder AddRocketWikiAi(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AiOptions>()
            .Bind(builder.Configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return builder;
    }
}
