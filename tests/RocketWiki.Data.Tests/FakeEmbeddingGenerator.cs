using Microsoft.Extensions.AI;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Deterministic <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> for driving the
/// REAL pipeline (chunker → indexer → store → hybrid retrieval) on SQLite, per
/// design.md §14: the embedding endpoint is the one dependency the integration tier
/// fakes. The vector function is supplied per test so semantic similarity is spelled
/// out in the test itself; every call's inputs are recorded so "re-embeds an edit
/// exactly once" is assertable as call counts, not vibes.
/// </summary>
internal sealed class FakeEmbeddingGenerator(Func<string, float[]> embed) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly List<string> _inputs = [];

    /// <summary>Every input string ever sent to this "endpoint", in order.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    /// <summary>Set to make the endpoint "unreachable" — §9.2's degrade path.</summary>
    public Exception? ThrowOnGenerate { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnGenerate is not null)
        {
            throw ThrowOnGenerate;
        }

        var list = values.ToList();
        _inputs.AddRange(list);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(v => new Embedding<float>(embed(v)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
