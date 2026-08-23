using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RocketWiki.Api.Embeddings;
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
/// </summary>
public sealed class EmbeddingPipelineWiringTests
{
    private static WebApplicationBuilder NewBuilder(Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Configuration.AddInMemoryCollection(config);
        return builder;
    }

    private static bool HasGenerator(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));

    private static EmbeddingOptions RegisteredOptions(IServiceCollection services) =>
        Assert.IsType<EmbeddingOptions>(services.Single(d => d.ServiceType == typeof(EmbeddingOptions)).ImplementationInstance);

    [Fact]
    public void NotConfigured_RegistersNothing()
    {
        var builder = NewBuilder(new Dictionary<string, string?>());

        builder.AddRocketWikiEmbeddings();

        Assert.False(HasGenerator(builder.Services));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(EmbeddingOptions));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(EmbeddingIndexer));
        Assert.DoesNotContain(builder.Services, d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EmbeddingBackgroundService));
    }

    [Fact]
    public void ConnectionString_InAiShape_ConfiguresEverything()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = "Endpoint=http://llm-gateway:8000/v1;Key=secret;Model=nomic-embed-text;Dimensions=768",
            ["Ai:PollSeconds"] = "5",
        });

        builder.AddRocketWikiEmbeddings();

        Assert.True(HasGenerator(builder.Services));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(EmbeddingIndexer));
        Assert.Contains(builder.Services, d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EmbeddingBackgroundService));

        var options = RegisteredOptions(builder.Services);
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

        builder.AddRocketWikiEmbeddings();

        Assert.True(HasGenerator(builder.Services));
        var options = RegisteredOptions(builder.Services);
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

        builder.AddRocketWikiEmbeddings();

        // Half a configuration embeds nothing rather than guessing a model name.
        Assert.False(HasGenerator(builder.Services));
    }
}
