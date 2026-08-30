using System.Diagnostics;
using System.Diagnostics.Metrics;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Telemetry;

/// <summary>
/// Operational instrumentation for the API host's own moving parts (design.md §15):
/// JIT user provisioning, SignalR presence, and notification fan-out. Everything else
/// the API does is already covered — HTTP by <c>AddAspNetCoreInstrumentation</c>, hub
/// method invocations by the built-in <c>Microsoft.AspNetCore.SignalR.Server</c>
/// ActivitySource, GraphQL by <c>HotChocolate.Diagnostics</c>, SQL by
/// <c>AddSqlClientInstrumentation</c>, and the domain by
/// <see cref="Core.Telemetry.CoreTelemetry"/>/<c>DataTelemetry</c>.
///
/// design.md §15 applies without exception: no page content, no titles, no search text,
/// no principal attribute values. Presence payloads carry a display name and colour over
/// the wire (§8) — neither appears here, because a *count* of viewers answers every
/// operational question a name would, without putting who-was-reading-what into a trace.
/// That distinction is the whole point of §15: the audit log (§7) is the record of who
/// did what, and it is the regulated one.
/// </summary>
public static class ApiTelemetry
{
    public const string SourceName = "RocketWiki.Api";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// design.md §11.3: the local User row is upserted from token claims on every
    /// authenticated request. A steady stream of <c>created</c> long after rollout means
    /// something is wrong with subject stability, which is otherwise invisible.
    /// </summary>
    public static readonly Counter<long> JitProvisionings =
        Meter.CreateCounter<long>("rocketwiki.identity.jit_provisionings", "{user}",
            "JIT user provisioning outcomes, by whether the local User row was created or refreshed.");

    public static readonly Counter<long> PresenceJoins =
        Meter.CreateCounter<long>("rocketwiki.presence.page_joins", "{join}",
            "Presence page-join attempts, by outcome.");

    public static readonly Counter<long> PresenceLeaves =
        Meter.CreateCounter<long>("rocketwiki.presence.page_leaves", "{leave}",
            "Presence page-leaves, by whether the client left explicitly or disconnected.");

    /// <summary>
    /// design.md §8: connections are evicted and re-authorized when access rules change.
    /// A rule change that evicts nobody and one that evicts everybody look identical in
    /// the audit log (both are one <c>accessrule.*</c> row); only this counter separates
    /// them.
    /// </summary>
    public static readonly Counter<long> PresenceEvictions =
        Meter.CreateCounter<long>("rocketwiki.presence.evictions", "{eviction}",
            "Presence connections evicted from a page group after an access-rule change.");

    public static readonly Counter<long> NotificationsFannedOut =
        Meter.CreateCounter<long>("rocketwiki.notifications.fanout", "{recipient}",
            "Per-recipient notification fan-out outcomes, by notification type and disposition.");

    /// <summary>
    /// design.md §8/§15: one measurement per MCP tool invocation, by tool and outcome.
    /// Both tags are bounded vocabularies (§15): the tool tag is only ever a name from
    /// <see cref="Mcp.McpToolAuditRegistry"/> — a client-supplied unknown tool name is
    /// collapsed to <see cref="McpToolUnknown"/> rather than minting a series per probe —
    /// and outcomes are the fixed set below. Never arguments, never content: the query a
    /// user searched for belongs in the audit row's Details (§7), nowhere else.
    /// </summary>
    public static readonly Counter<long> McpToolCalls =
        Meter.CreateCounter<long>("rocketwiki.mcp.tool_calls", "{call}",
            "MCP tool invocations, by tool name and outcome.");

    public static readonly Histogram<double> McpToolCallDuration =
        Meter.CreateHistogram<double>("rocketwiki.mcp.tool_call.duration", "s",
            "Duration of MCP tool invocations, by tool name and outcome.");

    /// <summary>
    /// design.md §15 (GitLab amendment): one measurement per GitLab fetch, by
    /// operation, outcome, and upstream HTTP status class — all three bounded
    /// vocabularies. Never a project path, file path, issue title, or filter text:
    /// those belong to the audit row (`gitlab.fetch`, §7), and the built-in
    /// HttpClient span is deliberately not emitted for this client at all
    /// (GitLabConfiguration) because its url.full would carry exactly them.
    /// </summary>
    public static readonly Counter<long> GitLabFetches =
        Meter.CreateCounter<long>("rocketwiki.gitlab.fetches", "{fetch}",
            "GitLab fetches, by operation, outcome, and upstream status class.");

    public static readonly Histogram<double> GitLabFetchDuration =
        Meter.CreateHistogram<double>("rocketwiki.gitlab.fetch.duration", "s",
            "Duration of GitLab fetches, by operation, outcome, and upstream status class.");

    /// <summary>Constant span name (low-cardinality, §15); operation/outcome ride as tags.</summary>
    public const string GitLabFetchSpan = "rocketwiki.gitlab.fetch";

    public const string GitLabOperationTag = "rocketwiki.gitlab.operation";
    public const string GitLabOutcomeTag = "rocketwiki.gitlab.outcome";
    public const string GitLabStatusClassTag = "rocketwiki.gitlab.status_class";

    public const string GitLabOperationIssue = "issue";
    public const string GitLabOperationIssues = "issues";
    public const string GitLabOperationFile = "file";

    public const string GitLabOutcomeOk = "ok";
    public const string GitLabOutcomeNotConfigured = "not_configured";
    public const string GitLabOutcomeNoCredential = "no_credential";
    public const string GitLabOutcomeInvalidCredential = "invalid_credential";
    public const string GitLabOutcomeNotFound = "not_found";
    public const string GitLabOutcomeUnreachable = "unreachable";

    public static void RecordGitLabFetch(string operation, string outcome, int? upstreamStatus, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new(GitLabOperationTag, operation),
            new(GitLabOutcomeTag, outcome),
            new(GitLabStatusClassTag, StatusClass(upstreamStatus)),
        };
        GitLabFetches.Add(1, tags);
        GitLabFetchDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>Collapses an HTTP status to its class — the §15 bounded-vocabulary rule
    /// applied to a value that is already an enum-ish int, but whose raw form would
    /// still mint a series per distinct upstream status.</summary>
    public static string StatusClass(int? status) => status switch
    {
        null => "none",
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        >= 500 => "5xx",
        _ => "none",
    };

    /// <summary>
    /// The anonymous Gravatar-protocol endpoint's one instrument (design: profile
    /// pictures): request count by outcome, a three-value bounded vocabulary
    /// (hit/miss/disabled). Never the hash, never an email, never a user id — the
    /// request is anonymous, so there is no audit row either, and this counter must
    /// not become the who-fetched-what record §7 deliberately doesn't keep for this
    /// route. The companion structural guarantee covers <b>traces and exported logs
    /// alike</b>: ServiceDefaults excludes <c>/avatar</c> from ASP.NET Core tracing
    /// entirely (the built-in server span's <c>url.path</c> would carry the email
    /// hash), and drops every log record emitted during such a request before it
    /// reaches the OpenTelemetry provider (with <c>IncludeScopes</c>, hosting's
    /// <c>RequestPath</c> scope would carry the same hash onto every line).
    /// </summary>
    public static readonly Counter<long> GravatarRequests =
        Meter.CreateCounter<long>("rocketwiki.avatars.gravatar_requests", "{request}",
            "Anonymous Gravatar-protocol avatar requests, by outcome.");

    public const string GravatarOutcomeTag = "rocketwiki.avatars.outcome";

    public const string GravatarOutcomeHit = "hit";
    public const string GravatarOutcomeMiss = "miss";
    public const string GravatarOutcomeDisabled = "disabled";

    public static void RecordGravatarRequest(string outcome) =>
        GravatarRequests.Add(1, new KeyValuePair<string, object?>(GravatarOutcomeTag, outcome));

    public const string JitResultTag = "rocketwiki.identity.result";
    public const string PresenceOutcomeTag = "rocketwiki.presence.outcome";
    public const string PresenceReasonTag = "rocketwiki.presence.reason";
    public const string NotificationTypeTag = "rocketwiki.notification.type";
    public const string NotificationDispositionTag = "rocketwiki.notification.disposition";

    // Span tags on the fan-out and presence-reauthorization spans. Constants rather than
    // literals at the SetTag call sites for the same reason as every other key here: §15's
    // naming rule is enforced by a test that reflects over these classes, and a literal
    // written at a call site is invisible to it. Counts only - never a recipient id, which
    // would turn a trace into the who-can-see-what record §7 owns.
    public const string NotificationCandidateCountTag = "rocketwiki.notification.candidate_count";
    public const string NotificationDeliveredCountTag = "rocketwiki.notification.delivered_count";
    public const string NotificationDeferredCountTag = "rocketwiki.notification.deferred_count";
    public const string PresenceConnectionCountTag = "rocketwiki.presence.connection_count";
    public const string PresenceEvictedCountTag = "rocketwiki.presence.evicted_count";

    public const string JitResultCreated = "created";
    public const string JitResultRefreshed = "refreshed";

    public const string NotificationFanOutSpan = "rocketwiki.notifications.fanout";
    public const string PresenceReauthorizeSpan = "rocketwiki.presence.reauthorize";

    /// <summary>Constant span name (low-cardinality, §15); the tool rides as a tag.</summary>
    public const string McpToolCallSpan = "rocketwiki.mcp.tool_call";

    public const string McpToolTag = "rocketwiki.mcp.tool";
    public const string McpOutcomeTag = "rocketwiki.mcp.outcome";

    /// <summary>Tool ran and produced a non-error result.</summary>
    public const string McpOutcomeSuccess = "success";

    /// <summary>Tool produced an error result — including the deliberate constant
    /// "not found" for absent/restricted subjects (§6.7 makes those one category by
    /// design; telemetry doesn't get to split what the caller can't).</summary>
    public const string McpOutcomeError = "error";

    /// <summary>Rejected by the filter's defense-in-depth check before any tool ran —
    /// should be unreachable behind the endpoint's RequireAuthorization.</summary>
    public const string McpOutcomeUnauthenticated = "unauthenticated";

    /// <summary>A registered tool with no audit declaration — refused, never run
    /// (design.md §7 fail-closed). A green build makes this unreachable.</summary>
    public const string McpOutcomeUndeclared = "undeclared";

    /// <summary>Call to a tool name this server doesn't register at all.</summary>
    public const string McpOutcomeUnknownTool = "unknown_tool";

    /// <summary>Stand-in tool tag for names outside the registry (unbounded client
    /// input must not become a metric dimension, §15).</summary>
    public const string McpToolUnknown = "unknown";

    public static void RecordMcpToolCall(string toolTag, string outcome, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new(McpToolTag, toolTag),
            new(McpOutcomeTag, outcome),
        };
        McpToolCalls.Add(1, tags);
        McpToolCallDuration.Record(duration.TotalSeconds, tags);
    }

    public static void RecordJitProvisioning(bool created) =>
        JitProvisionings.Add(1, new KeyValuePair<string, object?>(
            JitResultTag, created ? JitResultCreated : JitResultRefreshed));

    /// <summary>
    /// <paramref name="outcome"/> is a fixed vocabulary: <c>joined</c>, or one of the
    /// reasons a join silently does nothing. design.md §8 makes a join on an
    /// unviewable page indistinguishable from one on a nonexistent page *to the caller*;
    /// the operator still needs to know the difference, and a count with no page id and
    /// no user id gives them that without weakening the caller-facing behaviour.
    /// </summary>
    public static void RecordPresenceJoin(string outcome) =>
        PresenceJoins.Add(1, new KeyValuePair<string, object?>(PresenceOutcomeTag, outcome));

    public static void RecordPresenceLeave(string reason) =>
        PresenceLeaves.Add(1, new KeyValuePair<string, object?>(PresenceReasonTag, reason));

    public static void RecordNotificationFanOut(NotificationType type, string disposition, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        NotificationsFannedOut.Add(count,
            new KeyValuePair<string, object?>(NotificationTypeTag, type.ToString()),
            new KeyValuePair<string, object?>(NotificationDispositionTag, disposition));
    }

    public const string PresenceJoined = "joined";
    public const string PresenceNoPrincipal = "no_principal";
    public const string PresenceNotViewable = "not_viewable";
    public const string PresenceNoLocalUser = "no_local_user";

    public const string PresenceLeaveExplicit = "explicit";
    public const string PresenceLeaveDisconnected = "disconnected";

    // --- Co-editing (design.md §8 CRDT co-editing; §15 discipline throughout) ---
    // Everything here is counts, byte SIZES, and bounded vocabularies. Update payloads
    // are page content in CRDT form and never appear in telemetry in any encoding;
    // a byte COUNT is a bounded operational fact — the same line §10's storage
    // instrumentation already draws ("duration, count and byte count per operation",
    // never the bytes themselves). Who edited what lives in the audit log
    // (page.edit_session.*, §7) and the PageRevisionContributor table, not here.

    public static readonly Counter<long> CoEditSessionsStarted =
        Meter.CreateCounter<long>("rocketwiki.coedit.sessions_started", "{session}",
            "Edit sessions created (first member joined).");

    public static readonly Counter<long> CoEditJoins =
        Meter.CreateCounter<long>("rocketwiki.coedit.joins", "{join}",
            "Edit-session join attempts, by outcome.");

    /// <summary>Same shape as presence evictions: the rule-change mutation is one audit
    /// row whether it evicted nobody or everybody; only this counter separates them.</summary>
    public static readonly Counter<long> CoEditEvictions =
        Meter.CreateCounter<long>("rocketwiki.coedit.evictions", "{eviction}",
            "Edit-session members evicted after an access-rule change removed their canEdit.");

    /// <summary>Payload SIZE distribution per relayed message, by kind (update vs
    /// awareness). Size is the §15-safe shadow of relay volume: it answers "is the
    /// relay hot, are updates huge" without a single content byte leaving the hub.</summary>
    public static readonly Histogram<long> CoEditRelayBytes =
        Meter.CreateHistogram<long>("rocketwiki.coedit.relay_bytes", "By",
            "Size of each relayed co-editing message, by kind.");

    public static readonly Counter<long> CoEditLogResets =
        Meter.CreateCounter<long>("rocketwiki.coedit.log_resets", "{reset}",
            "Update-log resets, by reason (cap-triggered reseed vs empty-session expiry).");

    public const string CoEditOutcomeTag = "rocketwiki.coedit.outcome";
    public const string CoEditKindTag = "rocketwiki.coedit.kind";
    public const string CoEditResetReasonTag = "rocketwiki.coedit.reset_reason";

    public const string CoEditJoined = "joined";
    public const string CoEditJoinNoPrincipal = "no_principal";
    public const string CoEditJoinNoLocalUser = "no_local_user";
    /// <summary>No such live page. Split from denied here (unlike the caller-facing
    /// response, which §6.7 keeps identical) for the same reason presence splits its
    /// silent branches: the operator needs to know which one is firing, and an
    /// aggregate count with no page/user id preserves the caller-facing shape.</summary>
    public const string CoEditJoinNotFound = "not_found";
    public const string CoEditJoinDenied = "denied";

    public const string CoEditKindUpdate = "update";
    public const string CoEditKindAwareness = "awareness";
    /// <summary>A message dropped for exceeding its size cap (CoEditOptions): recorded
    /// on the relay histogram under this kind with its offending size, so a client
    /// stuck above the cap is operator-visible rather than silently mute.</summary>
    public const string CoEditKindOversized = "oversized";

    public const string CoEditLogResetCapReseed = "cap_reseed";
    public const string CoEditLogResetExpired = "expired";

    public static void RecordCoEditJoin(string outcome) =>
        CoEditJoins.Add(1, new KeyValuePair<string, object?>(CoEditOutcomeTag, outcome));

    public static void RecordCoEditRelay(string kind, long sizeBytes) =>
        CoEditRelayBytes.Record(sizeBytes, new KeyValuePair<string, object?>(CoEditKindTag, kind));

    public static void RecordCoEditLogReset(string reason) =>
        CoEditLogResets.Add(1, new KeyValuePair<string, object?>(CoEditResetReasonTag, reason));

    // --- "Ask the wiki" assistant (design.md §9 assistant; §15 discipline) ---
    // Dispositions, counts and durations only. The question, the retrieved content,
    // and the citations are exactly the §15-forbidden categories (search text, page
    // content, titles); they live in the assistant.ask audit row's Details (§7) and
    // nowhere else — AssistantTelemetryHygieneTests sweeps every source and meter
    // for them.

    public static readonly Counter<long> AssistantAsks =
        Meter.CreateCounter<long>("rocketwiki.assistant.asks", "{ask}",
            "Ask-the-wiki requests, by disposition.");

    /// <summary>How many pages' content entered the model context per ask — the
    /// retrieval-count signal ("is retrieval finding anything") with no page
    /// identity attached.</summary>
    public static readonly Histogram<long> AssistantRetrievedPages =
        Meter.CreateHistogram<long>("rocketwiki.assistant.retrieved_pages", "{page}",
            "Pages whose content entered the model context, per ask, by disposition.");

    public static readonly Histogram<double> AssistantAskDuration =
        Meter.CreateHistogram<double>("rocketwiki.assistant.ask.duration", "s",
            "Duration of ask-the-wiki requests end to end (retrieval + generation), by disposition.");

    /// <summary>Constant span name (low-cardinality, §15); disposition rides as a tag.</summary>
    public const string AssistantAskSpan = "rocketwiki.assistant.ask";

    public const string AssistantDispositionTag = "rocketwiki.assistant.disposition";

    public const string AssistantDispositionAnswered = "answered";
    public const string AssistantDispositionNoResults = "no_results";
    public const string AssistantDispositionNotConfigured = "not_configured";
    public const string AssistantDispositionUnreachable = "unreachable";

    /// <summary>Refused for exceeding Ai:MaxQuestionChars; nothing retrieved, nothing sent.</summary>
    public const string AssistantDispositionQuestionTooLong = "question_too_long";

    /// <summary>
    /// The ask threw rather than returning a disposition — the caller disconnected
    /// mid-generation, or retrieval faulted (a vector-dimension mismatch propagates
    /// deliberately). Used only in the §7 audit row, never as a telemetry tag: the ask
    /// never reached the point where it records one, and inventing a metric measurement
    /// for a request that did not complete would be a different kind of dishonest.
    /// </summary>
    public const string AssistantDispositionAbandoned = "abandoned";

    public static void RecordAssistantAsk(string disposition, int retrievedPageCount, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new(AssistantDispositionTag, disposition),
        };
        AssistantAsks.Add(1, tags);
        AssistantRetrievedPages.Record(retrievedPageCount, tags);
        AssistantAskDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>Recipient was connected and passed canView at send time: row persisted
    /// with a title snapshot and pushed to their live connection.</summary>
    public const string NotificationDeliveredLive = "delivered_live";

    /// <summary>
    /// Recipient had no open connection, so no live Principal existed to evaluate
    /// canView against at send time. A deferred row was persisted with no title
    /// snapshot; both its existence and its title are resolved at the recipient's next
    /// notifications fetch against their live token-built Principal
    /// (NotificationReadModelService) — the same pattern as sync-imported rows
    /// (design.md §8). This counter measures how much of the fan-out is deferred, not
    /// how much is lost: nothing is dropped on this path.
    /// </summary>
    public const string NotificationDeferredOffline = "deferred_offline";

    /// <summary>Skipped because canView failed for that connected recipient at send time
    /// (design.md §8): no row, no push — the only disposition that persists nothing.</summary>
    public const string NotificationSkippedNotViewable = "skipped_not_viewable";
}
