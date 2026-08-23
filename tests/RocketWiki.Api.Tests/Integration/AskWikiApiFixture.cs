using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Assistant;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Deterministic, fully inspectable stand-in for the chat endpoint. Its transcript
/// (<see cref="Calls"/>) is the adversarial centerpiece of AskWikiQueryTests: every
/// message the ask pipeline ever sent to the "model" is captured verbatim, so a test
/// can assert restricted content NEVER reached it — the strongest available proof
/// that retrieval ran under the caller's principal, because the model cannot leak
/// what it was never given. Scriptable response (<see cref="Respond"/>) and failure
/// (<see cref="ThrowOnCall"/>) because the fixture is shared across a test class.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private static readonly Regex MarkerRegex = new(@"\[S\d+\]", RegexOptions.Compiled);

    private readonly List<IReadOnlyList<ChatMessage>> _calls = [];

    /// <summary>Default: a grounded-looking answer citing every marker the prompt
    /// offered — so citation assertions can compare against exactly what retrieval
    /// handed over, without per-test scripting.</summary>
    public Func<IReadOnlyList<ChatMessage>, string> Respond { get; set; } = CiteEverything;

    public Exception? ThrowOnCall { get; set; }

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToList();
            }
        }
    }

    public int CallCount
    {
        get
        {
            lock (_calls)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>Every piece of text this client was ever sent, flattened — the
    /// haystack the adversarial tests sweep for restricted sentinels.</summary>
    public string Transcript =>
        string.Join("\n", Calls.SelectMany(call => call).Select(m => m.Text));

    public void Reset()
    {
        lock (_calls)
        {
            _calls.Clear();
        }

        Respond = CiteEverything;
        ThrowOnCall = null;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = messages.ToList();
        lock (_calls)
        {
            _calls.Add(call);
        }

        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond(call))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("askWiki v1 is deliberately non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static string CiteEverything(IReadOnlyList<ChatMessage> messages)
    {
        var markers = messages
            .SelectMany(m => MarkerRegex.Matches(m.Text).Select(match => match.Value))
            .Distinct()
            .ToList();

        return markers.Count == 0
            ? "I could not find the answer in the wiki."
            : "Grounded answer from the provided context " + string.Concat(markers) + ".";
    }
}

/// <summary>
/// The standard SQLite tier with the assistant configured against
/// <see cref="FakeChatClient"/> — i.e. exactly what AssistantConfiguration registers
/// when an endpoint exists, minus the OpenAI-backed client. Retrieval stays REAL:
/// the genuine SearchService (LIKE fallback) and PageReadService run the genuine
/// canView filtering, which is the entire point of the adversarial tests. The base
/// (unconfigured) factory stays reachable via <see cref="BaseFactory"/> for
/// NOT_CONFIGURED tests, the GitLabApiFixture pattern.
/// </summary>
public sealed class AskWikiApiFixture : IDisposable
{
    public RocketWikiApiFactory BaseFactory { get; } = new();

    public FakeChatClient ChatClient { get; } = new();

    public AssistantOptions Options { get; } =
        new("fake-chat-model", TimeSpan.FromSeconds(5), MaxContextChars: 24_000, MaxRetrievedPages: 8);

    public WebApplicationFactory<Program> Factory { get; }

    public AskWikiApiFixture()
    {
        Factory = BaseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient>(ChatClient);
            services.AddSingleton(Options);
        }));
    }

    public void Dispose()
    {
        Factory.Dispose();
        BaseFactory.Dispose();
        ChatClient.Dispose();
    }
}
