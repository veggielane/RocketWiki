using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Search;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Deterministic embedding "endpoint" for the API tier. The vector function is
/// swappable per test (<see cref="Embed"/>) because the fixture — and therefore this
/// singleton — is shared across a test class; <see cref="ThrowOnGenerate"/> simulates
/// §9.2's unreachable-endpoint case.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 4;

    /// <summary>Default: a content-derived deterministic vector, so any text embeds without per-test setup.</summary>
    public Func<string, float[]> Embed { get; set; } = text =>
        [(text.Length % 7) + 1f, (text.Length % 5) + 1f, (text.Length % 3) + 1f, 1f];

    public Exception? ThrowOnGenerate { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnGenerate is not null)
        {
            throw ThrowOnGenerate;
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(Embed(v)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// The standard <see cref="RocketWikiApiFactory"/> tier (real API, SQLite, fake auth)
/// with the embedding pipeline configured against a fake generator — i.e. exactly what
/// EmbeddingPipelineConfiguration registers when an endpoint exists, minus the
/// OpenAI-backed generator (swapped for <see cref="FakeEmbeddingGenerator"/>) and minus
/// the polling hosted service: tests call <see cref="RunIndexerAsync"/> for a
/// deterministic "the job ran" instead of racing a timer. SearchService in this host
/// therefore runs the REAL hybrid path.
/// </summary>
public sealed class EmbeddingApiFixture : IDisposable
{
    private readonly RocketWikiApiFactory _base = new();

    public EmbeddingApiFixture()
    {
        Generator = new FakeEmbeddingGenerator();
        Options = new EmbeddingOptions("fake-model", FakeEmbeddingGenerator.Dimensions, FailureBackoff: TimeSpan.Zero);
        Factory = _base.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Generator);
            services.AddSingleton(Options);
            services.AddScoped<EmbeddingIndexer>();
        }));
    }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeEmbeddingGenerator Generator { get; }

    public EmbeddingOptions Options { get; }

    public async Task<EmbeddingIndexRun> RunIndexerAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EmbeddingIndexer>().RunOnceAsync();
    }

    public void Dispose()
    {
        Factory.Dispose();
        _base.Dispose();
    }
}
