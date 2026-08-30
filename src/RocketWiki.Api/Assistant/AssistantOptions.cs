namespace RocketWiki.Api.Assistant;

/// <summary>
/// "Ask the wiki" assistant settings (design.md §9/§17's assistant bullet). Registered
/// by <see cref="AssistantConfiguration"/> ONLY when a chat endpoint + model are
/// configured — §15's fail-closed family: no default endpoint exists, and unset means
/// the feature is absent (askWiki answers NOT_CONFIGURED), never a guess at a
/// destination. The user's question and viewable page content are sent to this
/// endpoint, so a defaulted or fallback value would ship regulated content to an
/// unreviewed destination — the same reasoning as GitLab:BaseUrl and the OTLP endpoint.
/// </summary>
/// <param name="ChatModel">Model name the OpenAI-compatible endpoint serves (Ai:ChatModel,
/// or Model= in the `assistant` connection string). Required — its absence is what
/// "not configured" means.</param>
/// <param name="Timeout">Per-call network timeout (Ai:ChatTimeoutSeconds, default 30 —
/// longer than GitLab's 5s because generation genuinely takes seconds, but still
/// bounded: an ask must degrade to UNREACHABLE, not hang the request).</param>
/// <param name="MaxContextChars">Cap on total context chunk text sent to the model per
/// ask (Ai:MaxContextChars, default 24000 ≈ 6k tokens at ~4 chars/token — the same
/// chars-not-tokens reasoning as ChunkerOptions).</param>
/// <param name="MaxRetrievedPages">How many permission-filtered hits retrieval asks
/// ISearchService for (Ai:MaxRetrievedPages, default 8).</param>
/// <param name="MaxOutputTokens">Cap on the answer the model may generate
/// (Ai:MaxOutputTokens, default 800). Without one the only bound was the 30s network
/// timeout, so a rambling model held the request open for the whole of it.</param>
/// <param name="MaxQuestionChars">Longest question this instance will accept
/// (Ai:MaxQuestionChars, default 2000 ≈ 500 tokens). The ONLY bound on it before this
/// existed was Kestrel’s default request size, so one authenticated user could send a
/// ~30 MB question that flowed into a LIKE pattern, the model request body, and
/// AuditEvent.DetailsJson — which is append-only and has no length limit of its own.
/// Writing megabytes per ask into the regulated record is the part that does not
/// clean up afterwards.</param>
public sealed record AssistantOptions(
    string ChatModel,
    TimeSpan Timeout,
    int MaxContextChars,
    int MaxRetrievedPages,
    int MaxQuestionChars = 2000,
    int MaxOutputTokens = 800);
