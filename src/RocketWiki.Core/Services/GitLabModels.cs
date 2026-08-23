namespace RocketWiki.Core.Services;

/// <summary>
/// One GitLab issue as the wiki renders it (design.md GitLab integration section).
/// <see cref="Project"/> echoes the caller's own project reference (numeric id or
/// full path — whatever the page's `gitlab-issue://` reference carried), not
/// GitLab's canonical id: the placeholder/degraded rendering must be able to show
/// the reference exactly as the page stores it.
/// </summary>
public sealed record GitLabIssue(
    string Project,
    int Iid,
    string Title,
    string State,
    IReadOnlyList<string> Labels,
    string? AuthorName,
    string? AuthorUsername,
    IReadOnlyList<string> AssigneeNames,
    string WebUrl,
    DateTime? CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    string? DueDate,
    string? Milestone,
    bool Confidential);

/// <summary>
/// A repository file fetched for embedding. <see cref="Content"/> is null when the
/// file exists but its content is deliberately withheld — <see cref="ContentIssue"/>
/// then says why (over the configured size cap, or not decodable as text). Metadata
/// still renders in that case, so the embed degrades legibly rather than vanishing.
/// </summary>
public sealed record GitLabFile(
    string Project,
    string FilePath,
    string? Ref,
    string? FileName,
    long SizeBytes,
    string? ContentSha256,
    string? LastCommitId,
    string? Content,
    GitLabContentIssue? ContentIssue);

public enum GitLabContentIssue
{
    /// <summary>File is larger than GitLab:MaxFileBytes — metadata only, no content.</summary>
    TooLarge = 1,

    /// <summary>Bytes are not valid UTF-8 text (or contain NUL) — the wiki embeds text files, not binaries.</summary>
    NotText = 2,
}

/// <summary>
/// Filter for an issue-list embed. Field names deliberately mirror both the GitLab
/// REST v4 list-issues query parameters they map onto and the `gitlab-issues` fence
/// keys the editor serializes (design.md GitLab integration section) — one
/// vocabulary end to end.
/// </summary>
public sealed record GitLabIssueFilter(
    string? State = null,
    IReadOnlyList<string>? Labels = null,
    string? Search = null,
    string? Milestone = null,
    string? OrderBy = null,
    string? Sort = null);

/// <summary>
/// Why a GitLab fetch produced no data, at the client level. NotConfigured /
/// NoCredential are decided a layer above (they never reach the HTTP client);
/// this enum covers what the wire can say.
/// </summary>
public enum GitLabFetchFailure
{
    /// <summary>GitLab answered 401 — the stored token is wrong, revoked, or expired.
    /// Surfaced distinctly so the user learns their token needs replacing.</summary>
    InvalidCredential = 1,

    /// <summary>GitLab answered 403 or 404 — deliberately collapsed: GitLab itself
    /// blurs the two for unauthorized resources, and the wiki must not sharpen an
    /// absent-vs-forbidden distinction upstream chose to blur (same instinct as §6.7).</summary>
    NotFound = 2,

    /// <summary>Network failure, timeout, 5xx, throttling, or an unparseable response —
    /// GitLab could not be usefully reached. The embed degrades to its placeholder.</summary>
    Unreachable = 3,
}

/// <summary>
/// Client-level result: data, or a typed failure with the upstream HTTP status when
/// one exists. Modeled like <see cref="ReadResult{T}"/> — pattern-match, no throwing:
/// a GitLab outage must degrade an embed, never break the page around it.
/// </summary>
public abstract record GitLabFetchResult<T> where T : class
{
    private GitLabFetchResult()
    {
    }

    public sealed record Ok(T Value) : GitLabFetchResult<T>;

    public sealed record Failed(GitLabFetchFailure Failure, int? UpstreamStatus) : GitLabFetchResult<T>;
}
