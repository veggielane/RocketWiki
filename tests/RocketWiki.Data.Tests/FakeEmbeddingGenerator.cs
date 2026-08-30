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

    /// <summary>
    /// Set to make the endpoint reject only the requests whose text contains this marker,
    /// the way a real provider rejects one page for an oversized chunk or a content
    /// filter while serving every other page normally. Distinct from
    /// <see cref="ThrowOnGenerate"/>: that one is "the endpoint is down", this one is
    /// "the endpoint is up and this page is the problem", and the indexer is required to
    /// tell them apart.
    /// </summary>
    public string? PoisonMarker { get; set; }

    /// <summary>
    /// Calls that reached the endpoint, including the ones it rejected — the only way to
    /// assert that a quarantined page is no longer being ATTEMPTED, as opposed to merely
    /// no longer succeeding. <see cref="Inputs"/> deliberately still records only what a
    /// call actually accepted.
    /// </summary>
    public int Attempts { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Attempts++;

        if (ThrowOnGenerate is not null)
        {
            throw ThrowOnGenerate;
        }

        var list = values.ToList();

        if (PoisonMarker is not null && list.Any(v => v.Contains(PoisonMarker, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The endpoint rejected this request.");
        }

        _inputs.AddRange(list);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(v => new Embedding<float>(embed(v)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
