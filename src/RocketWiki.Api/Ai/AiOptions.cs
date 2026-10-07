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
///
/// <para><b>Three layers, per value.</b> The Aspire connection string first; then the
/// feature's own section (<see cref="Embeddings"/> = <c>Ai:Embeddings:*</c>,
/// <see cref="Assistant"/> = <c>Ai:Assistant:*</c>); then the shared fallbacks beneath
/// both (<see cref="BaseUrl"/>, <see cref="ApiKey"/>, and the older per-feature model
/// spellings <see cref="EmbeddingModel"/> / <see cref="ChatModel"/>). The shared layer is
/// the common deployment — one gateway serving both models — and the per-feature layer
/// is for the instance whose embedding model and chat model live on different servers,
/// or take different keys, and which has no Aspire to hand it connection strings.</para>
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    // --- Per-feature endpoint settings (win over the shared fallbacks below) ---

    /// <summary>The embedding endpoint's own settings: <c>Ai:Embeddings:Endpoint</c>,
    /// <c>Ai:Embeddings:ApiKey</c>, <c>Ai:Embeddings:Model</c>, <c>Ai:Embeddings:Dimensions</c>.
    /// Each value beats the shared fallback beneath it and loses to the <c>embeddings</c>
    /// connection string above it.</summary>
    public AiEmbeddingsEndpointOptions Embeddings { get; set; } = new();

    /// <summary>The chat endpoint's own settings: <c>Ai:Assistant:Endpoint</c>,
    /// <c>Ai:Assistant:ApiKey</c>, <c>Ai:Assistant:Model</c>. Same precedence as
    /// <see cref="Embeddings"/>, against the <c>assistant</c> connection string.</summary>
    public AiEndpointOptions Assistant { get; set; } = new();

    // --- Shared fallbacks (fail-closed; the layers above win per value) ---

    /// <summary>Fallback endpoint for both clients when neither the connection string nor the
    /// feature's own section carries one.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Fallback credential. Absent means a keyless gateway: a placeholder is sent.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Embedding model name — the older spelling of <c>Ai:Embeddings:Model</c>, kept
    /// as its fallback. With neither, and no <c>Model=</c> in the connection string, the
    /// absence is what "embeddings not configured" means.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Chat model name — the older spelling of <c>Ai:Assistant:Model</c>, kept as its
    /// fallback. With neither, and no <c>Model=</c> in the connection string, the absence is
    /// what "assistant not configured" means.</summary>
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

    /// <summary>
    /// What every AI endpoint value has to be: an absolute <c>http://</c> or
    /// <c>https://</c> URL — the OpenAI-compatible API root, typically ending in
    /// <c>/v1</c>. Null and empty pass (unset is a supported state); anything else, such
    /// as the classic scheme-less <c>host:port</c> paste, fails the host at boot with the
    /// key named (<see cref="AiConfiguration"/>) rather than as a <c>UriFormatException</c>
    /// on the first embedding or question — the <c>FileStorage:S3:ServiceUrl</c> precedent.
    /// </summary>
    public static bool IsAbsoluteHttpUrlOrUnset(string? value) =>
        string.IsNullOrEmpty(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private static TimeSpan? Seconds(int? value) => value is { } s ? TimeSpan.FromSeconds(s) : null;
}

/// <summary>
/// One AI feature's own endpoint settings (<c>Ai:Assistant:*</c>, and the base of
/// <c>Ai:Embeddings:*</c>). Every value is optional and fail-closed in the same way the
/// shared keys are: an unset endpoint or model at every layer means the feature is
/// absent, never guessed. See <see cref="AiOptions"/> for the precedence.
/// </summary>
public class AiEndpointOptions
{
    /// <summary>Absolute http(s) URL of the OpenAI-compatible API root, e.g.
    /// <c>http://llm.internal:8000/v1</c>. Validated at startup.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The credential for this endpoint. Absent means keyless: a placeholder is sent.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The model name this endpoint is asked for.</summary>
    public string? Model { get; set; }
}

/// <summary>The embedding endpoint's settings: an <see cref="AiEndpointOptions"/> plus the
/// vector width, which is the one value that only an embedding endpoint declares.</summary>
public sealed class AiEmbeddingsEndpointOptions : AiEndpointOptions
{
    /// <summary>Vector width requested of this endpoint. Beats <c>Ai:Dimensions</c>, loses
    /// to the connection string's <c>Dimensions=</c>; must match the <c>vector(1536)</c>
    /// column on SQL Server (EmbeddingDimensionsStartupCheck). Validated at startup —
    /// explicitly, since DataAnnotations does not descend into a nested section.</summary>
    public int? Dimensions { get; set; }
}
