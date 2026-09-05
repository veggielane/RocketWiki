using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Ai;
using RocketWiki.Api.Assistant;
using RocketWiki.Api.Features;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// AssistantConfiguration's registration logic (design.md §9.5/§15) — the assistant
/// half of EmbeddingPipelineWiringTests, same shape: what registers when a chat
/// endpoint and model are configured, that nothing registers otherwise, that the
/// <c>AskWiki</c> feature flag registers the same nothing when off and turns nothing on
/// by itself, and that the tuning keys are resolved lazily from
/// <c>IOptions&lt;AiOptions&gt;</c> while the endpoint keys are read eagerly.
/// AskWikiService itself is always registered — the schema does not change shape with
/// configuration — which is asserted in every case.
/// </summary>
public sealed class AssistantWiringTests
{
    private const string FullConnectionString = "Endpoint=http://llm-gateway:8000/v1;Key=secret;Model=llama-3.3-70b-instruct";

    private static WebApplicationBuilder NewBuilder(Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddRocketWikiAi();
        return builder;
    }

    private static bool HasChatClient(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IChatClient));

    private static bool HasOptions(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(AssistantOptions));

    private static bool HasService(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(AskWikiService));

    private static AssistantOptions ResolvedOptions(WebApplicationBuilder builder)
    {
        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<AssistantOptions>();
    }

    [Fact]
    public void NotConfigured_RegistersTheServiceButNoClientOrOptions()
    {
        var builder = NewBuilder([]);

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled);

        Assert.True(HasService(builder.Services));
        Assert.False(HasChatClient(builder.Services));
        Assert.False(HasOptions(builder.Services));
    }

    [Fact]
    public void ConnectionString_ConfiguresClientAndOptions_WithTheDocumentedDefaults()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:assistant"] = FullConnectionString,
        });

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled);

        Assert.True(HasService(builder.Services));
        Assert.True(HasChatClient(builder.Services));

        var options = ResolvedOptions(builder);
        Assert.Equal("llama-3.3-70b-instruct", options.ChatModel);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(24_000, options.MaxContextChars);
        Assert.Equal(8, options.MaxRetrievedPages);
        Assert.Equal(2000, options.MaxQuestionChars);
        Assert.Equal(800, options.MaxOutputTokens);
    }

    [Fact]
    public void BareUrl_PlusAiChatModel_FillsTheGaps()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:assistant"] = "http://llm-gateway:8000/v1",
            ["Ai:ChatModel"] = "mistral",
        });

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled);

        Assert.True(HasChatClient(builder.Services));
        Assert.Equal("mistral", ResolvedOptions(builder).ChatModel);
    }

    [Fact]
    public void EndpointWithoutModel_StaysUnconfigured()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Ai:BaseUrl"] = "http://llm-gateway:8000/v1",
        });

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled);

        Assert.True(HasService(builder.Services));
        Assert.False(HasChatClient(builder.Services));
    }

    /// <summary>Flag off, fully configured: the unconfigured container. No client exists
    /// to carry a question anywhere.
    /// <para>Mutation-tested: drop the flag check in AddRocketWikiAssistant and this fails.</para></summary>
    [Fact]
    public void FlagOff_WithAFullConnectionString_RegistersTheServiceButNoClientOrOptions()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:assistant"] = FullConnectionString,
        });

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled with { AskWiki = false });

        Assert.True(HasService(builder.Services));
        Assert.False(HasChatClient(builder.Services));
        Assert.False(HasOptions(builder.Services));
    }

    [Fact]
    public void FlagOn_WithNothingConfigured_StillRegistersNoClient()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["FeatureManagement:AskWiki"] = "true",
        });

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.Evaluate(builder.Configuration));

        Assert.False(HasChatClient(builder.Services));
    }

    /// <summary>
    /// Tuning is read when AssistantOptions is resolved, from the one validated
    /// IOptions&lt;AiOptions&gt; registration — so configuration that lands after the
    /// wiring ran (a test host's) is what the ask surface enforces and reports.
    /// <para>Mutation-tested: read Ai:MaxQuestionChars off builder.Configuration inside
    /// AddRocketWikiAssistant and this fails with the default 2000.</para>
    /// </summary>
    [Fact]
    public void TuningKeys_AreResolvedLazily_SoLateConfigurationIsHonoured()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:assistant"] = FullConnectionString,
        });

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:ChatTimeoutSeconds"] = "11",
            ["Ai:MaxContextChars"] = "1234",
            ["Ai:MaxRetrievedPages"] = "3",
            ["Ai:MaxQuestionChars"] = "123",
            ["Ai:MaxOutputTokens"] = "77",
        });

        var options = ResolvedOptions(builder);
        Assert.Equal(TimeSpan.FromSeconds(11), options.Timeout);
        Assert.Equal(1234, options.MaxContextChars);
        Assert.Equal(3, options.MaxRetrievedPages);
        Assert.Equal(123, options.MaxQuestionChars);
        Assert.Equal(77, options.MaxOutputTokens);
    }

    /// <summary>The endpoint keys decide registration and are therefore eager — pinned,
    /// as in EmbeddingPipelineWiringTests, so the category cannot drift silently.</summary>
    [Fact]
    public void EndpointKeys_AreReadEagerly_SoALateEndpointIsNotSeen()
    {
        var builder = NewBuilder([]);

        builder.AddRocketWikiAssistant(FeatureFlagSnapshot.AllEnabled);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:assistant"] = FullConnectionString,
        });

        Assert.False(HasChatClient(builder.Services));
    }
}
