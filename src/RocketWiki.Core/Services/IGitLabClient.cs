namespace RocketWiki.Core.Services;

/// <summary>
/// Typed client over the GitLab REST v4 API (design.md GitLab integration section).
/// Provider-specific HTTP lives behind this interface (the §14 rule that anything
/// external stays behind an interface), implemented in RocketWiki.Api over
/// HttpClient and faked wholesale in tests.
///
/// <b>The credential is a per-call argument, never client state.</b> Every fetch
/// runs under the CALLING user's own GitLab token — the load-bearing decision of
/// the integration: a client that held a credential as a field would be one DI
/// lifetime bug away from cross-user reuse, which is the §6 read-around in a new
/// coat. Callers (the GraphQL resolvers) resolve the acting user's token first and
/// never call this without one.
///
/// <paramref name="project"/> throughout is either a numeric GitLab project id or
/// a full namespaced path ("group/subgroup/project") — the two forms GitLab's own
/// API accepts; implementations URL-encode it as a single path segment.
/// </summary>
public interface IGitLabClient
{
    Task<GitLabFetchResult<GitLabIssue>> GetIssueAsync(
        string project, int iid, string token, CancellationToken cancellationToken);

    /// <summary>First page only, capped by <paramref name="first"/> (the resolver
    /// enforces its own maximum before calling). An issue-list embed shows the top
    /// N; the full list lives in GitLab.</summary>
    Task<GitLabFetchResult<IReadOnlyList<GitLabIssue>>> ListIssuesAsync(
        string project, GitLabIssueFilter filter, int first, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches file content + metadata via the raw endpoint (metadata from the
    /// X-Gitlab-* response headers), size-capped: a file over the configured cap
    /// returns metadata with null content and <see cref="GitLabContentIssue.TooLarge"/> —
    /// the cap is enforced before the body is buffered, not after.
    /// </summary>
    Task<GitLabFetchResult<GitLabFile>> GetFileAsync(
        string project, string filePath, string? gitRef, string token, CancellationToken cancellationToken);
}
