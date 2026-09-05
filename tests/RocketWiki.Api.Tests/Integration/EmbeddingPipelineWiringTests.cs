using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RocketWiki.Api.Ai;
using RocketWiki.Api.Embeddings;
using RocketWiki.Api.Features;
using RocketWiki.Core.Search;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// EmbeddingPipelineConfiguration's registration logic (design.md §9.2/§15): what the
/// Aspire "embeddings" connection string and the Ai section resolve to, and — just as
/// load-bearing — that an unconfigured instance registers NOTHING, which is the
/// structural form of "search degrades to keyword-only" (the default test factory and
/// every pre-milestone-7 deployment rely on exactly that absence).
///
/// <para>Two things joined it. The <c>SemanticSearch</c> feature flag registers the same
/// nothing when off, whatever is configured, and turns nothing on by itself. And the
/// tuning keys moved into <c>AiOptions</c> and are read LAZILY from
/// <c>IOptions&lt;AiOptions&gt;</c> — the tests below prove that by adding configuration
/// AFTER the wiring call and watching the resolved options reflect it, which is the
/// shape a WebApplicationFactory's late configuration takes. The endpoint keys stay
/// eager (they decide registration, which happens before Build()), and that is pinned
/// too, so changing either category means changing docs/CONFIGURATION.md deliberately.</para>
/// </summary>
public sealed class EmbeddingPipelineWiringTests
{
    private const string FullConnectionString =
        "Endpoint=http://llm-gateway:8000/v1;Key=secret;Model=nomic-embed-text;Dimensions=768";

    private static WebApplicationBuilder NewBuilder(Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Configuration.AddInMemoryCollection(config);
        // The shared Ai registration Program.cs makes ahead of both AI features: the
        // lazily-resolved IOptions<AiOptions> the pipeline's factories read.
        builder.AddRocketWikiAi();
        return builder;
    }

    private static bool HasGenerator(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));

    private static bool HasBackgroundJob(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EmbeddingBackgroundService));

    /// <summary>Resolves, rather than reads the descriptor: EmbeddingOptions is now a
    /// factory over IOptions&lt;AiOptions&gt;, so the value exists only when asked for.</summary>
    private static EmbeddingOptions ResolvedOptions(WebApplicationBuilder builder)
    {
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(EmbeddingOptions));
        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<EmbeddingOptions>();
    }

    [Fact]
    public void NotConfigured_RegistersNothing()
    {
        var builder = NewBuilder([]);

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled);

        Assert.False(HasGenerator(builder.Services));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(EmbeddingOptions));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(EmbeddingIndexer));
        Assert.False(HasBackgroundJob(builder.Services));
    }

    [Fact]
    public void ConnectionString_InAiShape_ConfiguresEverything()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = FullConnectionString,
            ["Ai:PollSeconds"] = "5",
        });

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled);

        Assert.True(HasGenerator(builder.Services));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(EmbeddingIndexer));
        Assert.True(HasBackgroundJob(builder.Services));

        var options = ResolvedOptions(builder);
        Assert.Equal("nomic-embed-text", options.Model);
        Assert.Equal(768, options.Dimensions);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PollIntervalOrDefault);
    }

    [Fact]
    public void BareUrlConnectionString_PlusAiSection_FillsTheGaps()
    {
        // §9.2's documented Ai section supplies model/dimensions; the Aspire-injected
        // value is just the endpoint (design.md §15 "config by reference").
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = "http://llm-gateway:8000/v1",
            ["Ai:EmbeddingModel"] = "bge-m3",
        });

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled);

        Assert.True(HasGenerator(builder.Services));
        var options = ResolvedOptions(builder);
        Assert.Equal("bge-m3", options.Model);
        Assert.Equal(1536, options.Dimensions); // §9.2's default, data-model.md's column size
    }

    [Fact]
    public void EndpointWithoutModel_StaysUnconfigured_FailingTowardKeywordOnly()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = "Endpoint=http://llm-gateway:8000/v1",
        });

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled);

        // Half a configuration embeds nothing rather than guessing a model name.
        Assert.False(HasGenerator(builder.Services));
    }

    /// <summary>
    /// The flag is an independent off-switch: a complete, valid endpoint configuration
    /// registers nothing when SemanticSearch is off — the same nothing as unconfigured,
    /// so no page content can travel to the endpoint (there is no client to carry it).
    /// <para>Mutation-tested: drop the flag check in AddRocketWikiEmbeddings and this fails.</para>
    /// </summary>
    [Fact]
    public void FlagOff_WithAFullConnectionString_RegistersNothing()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = FullConnectionString,
        });

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled with { SemanticSearch = false });

        Assert.False(HasGenerator(builder.Services));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(EmbeddingOptions));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(EmbeddingIndexer));
        Assert.False(HasBackgroundJob(builder.Services));
    }

    /// <summary>The flag turns nothing on: explicitly enabled with no endpoint is still
    /// unconfigured. There is nowhere for a flag alone to send anything.</summary>
    [Fact]
    public void FlagOn_WithNothingConfigured_StillRegistersNothing()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["FeatureManagement:SemanticSearch"] = "true",
        });

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.Evaluate(builder.Configuration));

        Assert.False(HasGenerator(builder.Services));
    }

    /// <summary>
    /// The tuning keys are read from IOptions&lt;AiOptions&gt; when EmbeddingOptions is
    /// resolved, not captured when the wiring ran — configuration added AFTER the wiring
    /// call (the moment a WebApplicationFactory's ConfigureAppConfiguration lands) is
    /// what the job runs with.
    /// <para>Mutation-tested: read Ai:BatchSize off builder.Configuration inside
    /// AddRocketWikiEmbeddings and this fails with the default 16.</para>
    /// </summary>
    [Fact]
    public void TuningKeys_AreResolvedLazily_SoLateConfigurationIsHonoured()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = FullConnectionString,
        });

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:BatchSize"] = "3",
            ["Ai:MaxAttempts"] = "9",
            ["Ai:FailureBackoffSeconds"] = "42",
            ["Ai:EmbeddingTimeoutSeconds"] = "7",
        });

        var options = ResolvedOptions(builder);
        Assert.Equal(3, options.BatchSize);
        Assert.Equal(9, options.MaxAttemptsOrDefault);
        Assert.Equal(TimeSpan.FromSeconds(42), options.FailureBackoffOrDefault);
        Assert.Equal(TimeSpan.FromSeconds(7), options.RequestTimeoutOrDefault);
    }

    /// <summary>
    /// The other category, pinned so it cannot change by accident: the ENDPOINT keys
    /// decide whether anything registers, so they are necessarily read when the wiring
    /// runs. An endpoint that arrives later is not seen — which is exactly why
    /// FeatureFlagOffStateTests reaches this path with UseSetting rather than
    /// ConfigureAppConfiguration, and why docs/CONFIGURATION.md says so.
    /// </summary>
    [Fact]
    public void EndpointKeys_AreReadEagerly_SoALateEndpointIsNotSeen()
    {
        var builder = NewBuilder([]);

        builder.AddRocketWikiEmbeddings(FeatureFlagSnapshot.AllEnabled);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = FullConnectionString,
        });

        Assert.False(HasGenerator(builder.Services));
    }
}
