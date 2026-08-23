using System.Diagnostics;
using System.Text.Json;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GitLab;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Why a GitLab field returned no data — the typed, legible degradation the design
/// requires (never a GraphQL error that eats the page, never cached content
/// masquerading as live). The SPA renders a placeholder showing the reference from
/// the page's own Markdown, which is all the placeholder ever needs.
/// </summary>
public enum GitLabUnavailableReason
{
    /// <summary>GitLab:BaseUrl is unset on this instance (§15 fail-closed: unset =
    /// feature absent). The steady state on a high-side replica, whose network
    /// cannot reach the low side's GitLab (§12).</summary>
    NotConfigured = 1,

    /// <summary>The caller has no stored GitLab token (or it no longer decrypts).
    /// The Settings surface is where they add one.</summary>
    NoCredential = 2,

    /// <summary>GitLab answered 401: the stored token is revoked or expired.</summary>
    InvalidCredential = 3,

    /// <summary>GitLab answered 403/404 — collapsed, mirroring GitLab's own
    /// absent-not-forbidden posture for unauthorized resources.</summary>
    NotFound = 4,

    /// <summary>Network failure, timeout, throttling, or 5xx.</summary>
    Unreachable = 5,
}

public sealed record GitLabUnavailableView(GitLabUnavailableReason Reason, int? UpstreamStatus);

public sealed record GitLabIssuePayload(GitLabIssue? Issue, GitLabUnavailableView? Unavailable);
public sealed record GitLabIssuesPayload(IReadOnlyList<GitLabIssue>? Issues, GitLabUnavailableView? Unavailable);
public sealed record GitLabFilePayload(GitLabFile? File, GitLabUnavailableView? Unavailable);

/// <summary>The Settings surface's read: is the integration configured here, and does
/// the caller have a token stored. Never the token itself — nothing returns it.</summary>
public sealed record GitLabStatus(bool Configured, string? BaseUrl, bool ViewerHasToken);

public partial class Query
{
    private const int DefaultIssueListSize = 20;

    // GitLab rejects unknown order_by/sort/state values with a 400, which would
    // surface as UNREACHABLE — misleading for what is a typo in a hand-edited
    // fence. Out-of-vocabulary values degrade to unset (GitLab's own defaults)
    // instead; the list still renders. Bounded sets from the REST v4 docs.
    private static readonly string[] AllowedIssueStates = ["opened", "closed", "all"];
    private static readonly string[] AllowedOrderBy =
        ["created_at", "updated_at", "priority", "due_date", "relative_position", "label_priority", "milestone_due", "popularity", "weight"];
    private static readonly string[] AllowedSort = ["asc", "desc"];

    /// <summary>
    /// design.md GitLab integration: live status for a `gitlab-issue://` reference,
    /// fetched at view time under the CALLER's own GitLab credential — never a
    /// service account (§6's read-around rule extended to external content). All
    /// degradations are typed payload facts, never GraphQL errors: an embed's
    /// failure must not eat the page around it.
    /// </summary>
    [AuditAction("gitlab.fetch")]
    public async Task<GitLabIssuePayload> GitlabIssue(
        string projectId,
        int iid,
        [Service] GitLabOptions options,
        [Service] IGitLabClient client,
        [Service] IGitLabCredentialService credentials,
        [Service] IActingUserAccessor actingUser,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var fetch = new GitLabFetch(ApiTelemetry.GitLabOperationIssue, $"issue:{projectId}:{iid}",
            new { operation = "issue", project = projectId, iid });

        var (token, gate) = await GateAsync(options, credentials, actingUser, cancellationToken);
        if (gate is not null)
        {
            return new GitLabIssuePayload(null, await fetch.DegradeAsync(gate.Value, actingUser, auditSink, cancellationToken));
        }

        var result = await fetch.MeasureAsync(() => client.GetIssueAsync(projectId, iid, token!, cancellationToken));
        if (result is GitLabFetchResult<GitLabIssue>.Failed failed)
        {
            return new GitLabIssuePayload(null, await fetch.FailAsync(failed.Failure, failed.UpstreamStatus, auditSink, cancellationToken));
        }

        var issue = ((GitLabFetchResult<GitLabIssue>.Ok)result).Value;
        await fetch.SucceedAsync(auditSink, cancellationToken);
        return new GitLabIssuePayload(issue, null);
    }

    /// <summary>
    /// The issue-list embed (`gitlab-issues` fence): first page only, server-capped —
    /// an embed shows the top N and links out to GitLab for the rest. Same
    /// per-user-credential and typed-degradation rules as <see cref="GitlabIssue"/>.
    /// </summary>
    [AuditAction("gitlab.fetch")]
    public async Task<GitLabIssuesPayload> GitlabIssues(
        string projectId,
        GitLabIssueFilter? filter,
        int? first,
        [Service] GitLabOptions options,
        [Service] IGitLabClient client,
        [Service] IGitLabCredentialService credentials,
        [Service] IActingUserAccessor actingUser,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var sanitized = Sanitize(filter ?? new GitLabIssueFilter());
        var pageSize = Math.Clamp(first ?? DefaultIssueListSize, 1, GitLabHttpClient.MaxPageSize);

        var fetch = new GitLabFetch(
            ApiTelemetry.GitLabOperationIssues,
            $"issues:{projectId}:{sanitized.State}:{JoinOrEmpty(sanitized.Labels)}:{sanitized.Search}:{sanitized.Milestone}:{sanitized.OrderBy}:{sanitized.Sort}:{pageSize}",
            new
            {
                operation = "issues",
                project = projectId,
                state = sanitized.State,
                labels = sanitized.Labels,
                search = sanitized.Search,
                milestone = sanitized.Milestone,
                first = pageSize,
            });

        var (token, gate) = await GateAsync(options, credentials, actingUser, cancellationToken);
        if (gate is not null)
        {
            return new GitLabIssuesPayload(null, await fetch.DegradeAsync(gate.Value, actingUser, auditSink, cancellationToken));
        }

        var result = await fetch.MeasureAsync(() => client.ListIssuesAsync(projectId, sanitized, pageSize, token!, cancellationToken));
        if (result is GitLabFetchResult<IReadOnlyList<GitLabIssue>>.Failed failed)
        {
            return new GitLabIssuesPayload(null, await fetch.FailAsync(failed.Failure, failed.UpstreamStatus, auditSink, cancellationToken));
        }

        var issues = ((GitLabFetchResult<IReadOnlyList<GitLabIssue>>.Ok)result).Value;
        await fetch.SucceedAsync(auditSink, cancellationToken, resultCount: issues.Count);
        return new GitLabIssuesPayload(issues, null);
    }

    /// <summary>
    /// The file embed (`gitlab-file` fence): content + metadata, size-capped
    /// (GitLab:MaxFileBytes — an over-cap or non-text file returns metadata with
    /// null content and a typed contentIssue, so the embed degrades legibly).
    /// </summary>
    [AuditAction("gitlab.fetch")]
    public async Task<GitLabFilePayload> GitlabFile(
        string projectId,
        string path,
        [GraphQLName("ref")] string? gitRef,
        [Service] GitLabOptions options,
        [Service] IGitLabClient client,
        [Service] IGitLabCredentialService credentials,
        [Service] IActingUserAccessor actingUser,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var fetch = new GitLabFetch(ApiTelemetry.GitLabOperationFile, $"file:{projectId}:{path}:{gitRef}",
            new { operation = "file", project = projectId, path, @ref = gitRef });

        var (token, gate) = await GateAsync(options, credentials, actingUser, cancellationToken);
        if (gate is not null)
        {
            return new GitLabFilePayload(null, await fetch.DegradeAsync(gate.Value, actingUser, auditSink, cancellationToken));
        }

        var result = await fetch.MeasureAsync(() => client.GetFileAsync(projectId, path, gitRef, token!, cancellationToken));
        if (result is GitLabFetchResult<GitLabFile>.Failed failed)
        {
            return new GitLabFilePayload(null, await fetch.FailAsync(failed.Failure, failed.UpstreamStatus, auditSink, cancellationToken));
        }

        var file = ((GitLabFetchResult<GitLabFile>.Ok)result).Value;
        await fetch.SucceedAsync(auditSink, cancellationToken, contentIssue: file.ContentIssue);
        return new GitLabFilePayload(file, null);
    }

    /// <summary>
    /// The Settings surface's status read: whether this instance has GitLab
    /// configured (and where), and whether the caller has a token stored. BaseUrl is
    /// this instance's own declared config — the same value the SPA needs to label
    /// the integration and build outbound GitLab links — not a discovery.
    /// </summary>
    [NoAudit("Reads instance config plus the caller's own has-a-token flag; no wiki content and nobody else's state (design.md §7). The token itself is structurally unreachable — no field returns it.")]
    public async Task<GitLabStatus> GitlabStatus(
        [Service] GitLabOptions options,
        [Service] IGitLabCredentialService credentials,
        [Service] IActingUserAccessor actingUser,
        CancellationToken cancellationToken)
    {
        var hasToken = actingUser.ActingUserId is { } userId
            && await credentials.HasTokenAsync(userId, cancellationToken);

        return new GitLabStatus(
            Configured: options.IsConfigured,
            BaseUrl: options.IsConfigured ? options.BaseUrl : null,
            ViewerHasToken: hasToken);
    }

    // --- plumbing shared by the three fetch fields ---

    /// <summary>NotConfigured/NoCredential, decided before the HTTP client is ever
    /// touched. Order matters: an unconfigured instance answers NOT_CONFIGURED even
    /// for anonymous callers — the feature is absent, there is nothing to have a
    /// credential for.</summary>
    private static async Task<(string? Token, GitLabUnavailableReason? Gate)> GateAsync(
        GitLabOptions options, IGitLabCredentialService credentials, IActingUserAccessor actingUser,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return (null, GitLabUnavailableReason.NotConfigured);
        }

        if (actingUser.ActingUserId is not { } userId)
        {
            return (null, GitLabUnavailableReason.NoCredential);
        }

        var token = await credentials.GetDecryptedTokenAsync(userId, cancellationToken);
        return token is null ? (null, GitLabUnavailableReason.NoCredential) : (token, null);
    }

    private static string JoinOrEmpty(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(',', values) : string.Empty;

    private static GitLabIssueFilter Sanitize(GitLabIssueFilter filter) => filter with
    {
        State = InVocabulary(filter.State, AllowedIssueStates),
        OrderBy = InVocabulary(filter.OrderBy, AllowedOrderBy),
        Sort = InVocabulary(filter.Sort, AllowedSort),
    };

    private static string? InVocabulary(string? value, string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : null;

    /// <summary>
    /// One GitLab fetch's cross-cutting record-keeping: the bounded §15 telemetry
    /// (span + counter/histogram — operation, outcome, status class; never a path,
    /// title, or query) and the §7 `gitlab.fetch` audit row (whose DetailsJson DOES
    /// carry the reference — project/iid/path/filter — exactly as search.query
    /// carries its query text: the audit table is the sanctioned record, telemetry
    /// is not). DedupKey keeps distinct resources in one request distinct in the
    /// audit log while the same resource fetched twice stays one row.
    /// </summary>
    private sealed class GitLabFetch(string operation, string dedupKey, object reference)
    {
        private TimeSpan _duration = TimeSpan.Zero;

        public async Task<T> MeasureAsync<T>(Func<Task<T>> call)
        {
            // Our own bounded span replaces the built-in HttpClient span this
            // client deliberately doesn't emit (GitLabConfiguration): constant
            // name, bounded tags, no URL.
            using var activity = ApiTelemetry.ActivitySource.StartActivity(ApiTelemetry.GitLabFetchSpan);
            activity?.SetTag(ApiTelemetry.GitLabOperationTag, operation);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                return await call();
            }
            finally
            {
                stopwatch.Stop();
                _duration = stopwatch.Elapsed;
            }
        }

        public Task SucceedAsync(
            IAuditSink auditSink, CancellationToken ct, int? resultCount = null, GitLabContentIssue? contentIssue = null) =>
            RecordAsync(auditSink, ApiTelemetry.GitLabOutcomeOk, null, resultCount, contentIssue, ct);

        public async Task<GitLabUnavailableView> FailAsync(
            GitLabFetchFailure failure, int? upstreamStatus, IAuditSink auditSink, CancellationToken ct)
        {
            var (reason, outcome) = failure switch
            {
                GitLabFetchFailure.InvalidCredential => (GitLabUnavailableReason.InvalidCredential, ApiTelemetry.GitLabOutcomeInvalidCredential),
                GitLabFetchFailure.NotFound => (GitLabUnavailableReason.NotFound, ApiTelemetry.GitLabOutcomeNotFound),
                _ => (GitLabUnavailableReason.Unreachable, ApiTelemetry.GitLabOutcomeUnreachable),
            };

            await RecordAsync(auditSink, outcome, upstreamStatus, null, null, ct);
            return new GitLabUnavailableView(reason, upstreamStatus);
        }

        /// <summary>NotConfigured / NoCredential: no HTTP happened. Still counted in
        /// telemetry (an operator watching a NO_CREDENTIAL surge learns onboarding is
        /// broken) and still audited when an acting user exists — an anonymous request
        /// has nobody to attribute a row to, and DbAuditSink refuses those by design.</summary>
        public async Task<GitLabUnavailableView> DegradeAsync(
            GitLabUnavailableReason gate, IActingUserAccessor actingUser, IAuditSink auditSink, CancellationToken ct)
        {
            var outcome = gate == GitLabUnavailableReason.NotConfigured
                ? ApiTelemetry.GitLabOutcomeNotConfigured
                : ApiTelemetry.GitLabOutcomeNoCredential;

            if (actingUser.ActingUserId is not null)
            {
                await RecordAsync(auditSink, outcome, null, null, null, ct);
            }
            else
            {
                ApiTelemetry.RecordGitLabFetch(operation, outcome, null, TimeSpan.Zero);
            }

            return new GitLabUnavailableView(gate, null);
        }

        private async Task RecordAsync(
            IAuditSink auditSink, string outcome, int? upstreamStatus, int? resultCount, GitLabContentIssue? contentIssue,
            CancellationToken ct)
        {
            ApiTelemetry.RecordGitLabFetch(operation, outcome, upstreamStatus, _duration);

            // The reference (project/iid/path/filter) belongs here and only here:
            // §7's Details column is the regulated record of what was fetched, the
            // same deliberate split as search.query text. Never the fetched CONTENT
            // — titles and file bodies are GitLab's data; the wiki records that the
            // fetch happened, not what came back.
            var detailsJson = JsonSerializer.Serialize(new
            {
                reference,
                outcome,
                upstreamStatus,
                resultCount,
                contentIssue = contentIssue?.ToString(),
            });

            await auditSink.RecordAsync(
                new AuditRecord("gitlab.fetch", AuditOutcome.Success, DetailsJson: detailsJson, DedupKey: dedupKey), ct);
        }
    }
}
