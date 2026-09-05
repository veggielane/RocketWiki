using System.ComponentModel.DataAnnotations;

namespace RocketWiki.Api.Ai;

/// <summary>
/// The <c>Ai</c> configuration section (design.md §9.2/§9.5, docs/CONFIGURATION.md
/// "AI"), bound once for both OpenAI-compatible endpoints — the embedding pipeline and
/// "ask the wiki" — because one gateway serving both models is the expected deployment.
/// Registered and validated by <see cref="AiConfiguration"/>; every default below
/// satisfies its own annotation, which keeps "no configuration at all" a working state
/// (design.md §15), and every explicit bad value fails the host at boot rather than
/// producing absurd behaviour later: a zero batch size embeds nothing forever, a zero
/// timeout fails every call, a zero context cap sends the model empty context.
///
/// <para><b>Two kinds of key, read at two moments.</b> The endpoint keys
/// (<see cref="BaseUrl"/>, <see cref="ApiKey"/>, <see cref="EmbeddingModel"/>,
/// <see cref="ChatModel"/>) decide whether a feature registers AT ALL, which has to happen
/// before <c>Build()</c>; they are bound eagerly off the builder through
/// <see cref="BindEagerly"/> — the one definition of shape and defaults, read early — and
/// they are fallbacks beneath the Aspire-injected connection strings, which stay on
/// <c>GetConnectionString</c> (the idiomatic API; see <see cref="AiConnectionStringParser"/>).
/// Every tuning key (timeouts, caps, batch size, poll interval, dimensions) is resolved
/// lazily through <c>IOptions&lt;AiOptions&gt;</c> where it is consumed, so a test host's
/// configuration is honoured and nothing is captured before it exists.</para>
///
/// <para>No endpoint has a default (§9.4): questions and page content travel to these
/// URLs, so an unresolved endpoint means "the feature is absent", never a guess.</para>
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    // --- Endpoint fallbacks (fail-closed; the connection string wins per value) ---

    /// <summary>Fallback endpoint for both clients when the connection string carries none.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Fallback credential. Absent means a keyless gateway: a placeholder is sent.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Embedding model name; with no <c>Model=</c> in the connection string, its
    /// absence is what "embeddings not configured" means.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Chat model name; with no <c>Model=</c> in the connection string, its
    /// absence is what "assistant not configured" means.</summary>
    public string? ChatModel { get; set; }

    // --- Embedding pipeline (design.md §9.2/§9.3) ---

    /// <summary>Vector width requested of the endpoint and validated per response. Null
    /// means the connection string's <c>Dimensions=</c>, else the column's fixed width
    /// (1536). A contradicting value fails startup on SQL Server (EmbeddingDimensionsStartupCheck).</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:Dimensions must be a positive vector width.")]
    public int? Dimensions { get; set; }

    /// <summary>Background job scan interval. Null means the job's 30 s default.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:PollSeconds must be a positive number of seconds.")]
    public int? PollSeconds { get; set; }

    /// <summary>Max pages (re-)embedded per job run.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:BatchSize must be a positive number of pages.")]
    public int BatchSize { get; set; } = 16;

    /// <summary>Retry delay for a page whose embedding failed. Null means the job's 5 min default.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:FailureBackoffSeconds must be a positive number of seconds.")]
    public int? FailureBackoffSeconds { get; set; }

    /// <summary>Consecutive failures on one revision before it is quarantined.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:MaxAttempts must be a positive number of attempts.")]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Per-call network timeout on the embedding endpoint. Null means the client's 30 s default.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:EmbeddingTimeoutSeconds must be a positive number of seconds.")]
    public int? EmbeddingTimeoutSeconds { get; set; }

    // --- "Ask the wiki" (design.md §9.5) ---

    /// <summary>Per-call network timeout on the chat endpoint. One attempt, no retries.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:ChatTimeoutSeconds must be a positive number of seconds.")]
    public int ChatTimeoutSeconds { get; set; } = 30;

    /// <summary>Cap on total context text sent to the model per ask (≈ 6k tokens).</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:MaxContextChars must be a positive number of characters.")]
    public int MaxContextChars { get; set; } = 24_000;

    /// <summary>Permission-filtered hits retrieval asks ISearchService for.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:MaxRetrievedPages must be a positive number of pages.")]
    public int MaxRetrievedPages { get; set; } = 8;

    /// <summary>Longest question accepted; over it, askWiki answers QUESTION_TOO_LONG
    /// before retrieval. Reported to the SPA by <c>assistantStatus.maxQuestionChars</c>.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:MaxQuestionChars must be a positive number of characters.")]
    public int MaxQuestionChars { get; set; } = 2000;

    /// <summary>Cap on the answer the model may generate.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Ai:MaxOutputTokens must be a positive number of tokens.")]
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>
    /// The eager bind for the two registration decisions (see the class doc). Same shape,
    /// same defaults as the <c>IOptions</c> registration — this is a second READ of the
    /// section, never a second definition of it — and the CoEditOptions precedent in
    /// Program.cs, where the SignalR transport limit needs the caps before the container
    /// exists. Validation is not repeated here: <c>ValidateOnStart</c> on the options
    /// registration fails the host on a bad value whichever path read it first.
    /// </summary>
    public static AiOptions BindEagerly(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<AiOptions>() ?? new AiOptions();

    public TimeSpan? PollInterval => Seconds(PollSeconds);
    public TimeSpan? FailureBackoff => Seconds(FailureBackoffSeconds);
    public TimeSpan? EmbeddingTimeout => Seconds(EmbeddingTimeoutSeconds);
    public TimeSpan ChatTimeout => TimeSpan.FromSeconds(ChatTimeoutSeconds);

    private static TimeSpan? Seconds(int? value) => value is { } s ? TimeSpan.FromSeconds(s) : null;
}
