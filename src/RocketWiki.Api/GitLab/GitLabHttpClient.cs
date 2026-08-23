using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GitLab;

/// <summary>
/// <see cref="IGitLabClient"/> over GitLab REST v4 (endpoint shapes verified against
/// docs.gitlab.com/api: <c>GET /projects/:id/issues/:issue_iid</c>,
/// <c>GET /projects/:id/issues</c>, <c>GET /projects/:id/repository/files/:file_path/raw</c>
/// with <c>X-Gitlab-*</c> metadata headers).
///
/// The per-user token arrives per call and goes into exactly one place: the
/// <c>PRIVATE-TOKEN</c> request header (GitLab's canonical PAT header; a future
/// OAuth credential would switch to <c>Authorization: Bearer</c> here and nowhere
/// else). It is never a default header on the underlying HttpClient — pooled
/// clients outlive requests, and a default header would be user A's token on
/// user B's fetch.
///
/// Failures map to <see cref="GitLabFetchResult{T}.Failed"/>, never exceptions:
/// 401 → InvalidCredential; 403/404 → NotFound (collapsed — GitLab itself blurs
/// them for unauthorized resources); anything else, timeouts, and network errors →
/// Unreachable. A GitLab outage degrades an embed, never the page around it.
/// </summary>
public sealed class GitLabHttpClient(HttpClient httpClient, GitLabOptions options) : IGitLabClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>GitLab's per_page ceiling for one page of an issue-list embed.</summary>
    public const int MaxPageSize = 50;

    public async Task<GitLabFetchResult<GitLabIssue>> GetIssueAsync(
        string project, int iid, string token, CancellationToken cancellationToken)
    {
        // Uri.EscapeDataString turns a namespaced path's '/' into %2F — the single
        // path segment GitLab's ":id" expects. Modern .NET's Uri preserves the
        // escaping (no %2F un-escaping on combine); pinned by test.
        var uri = $"projects/{Uri.EscapeDataString(project)}/issues/{iid}";

        return await SendAsync(uri, token, async response =>
        {
            var dto = await response.Content.ReadFromJsonAsync<IssueDto>(JsonOptions, cancellationToken);
            return dto is null
                ? (GitLabFetchResult<GitLabIssue>)new GitLabFetchResult<GitLabIssue>.Failed(GitLabFetchFailure.Unreachable, (int)response.StatusCode)
                : new GitLabFetchResult<GitLabIssue>.Ok(ToIssue(dto, project));
        }, cancellationToken);
    }

    public async Task<GitLabFetchResult<IReadOnlyList<GitLabIssue>>> ListIssuesAsync(
        string project, GitLabIssueFilter filter, int first, string token, CancellationToken cancellationToken)
    {
        var query = new StringBuilder();
        Append(query, "per_page", Math.Clamp(first, 1, MaxPageSize).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(query, "state", filter.State);
        Append(query, "labels", filter.Labels is { Count: > 0 } labels ? string.Join(',', labels) : null);
        Append(query, "search", filter.Search);
        Append(query, "milestone", filter.Milestone);
        Append(query, "order_by", filter.OrderBy);
        Append(query, "sort", filter.Sort);

        var uri = $"projects/{Uri.EscapeDataString(project)}/issues?{query}";

        return await SendAsync(uri, token, async response =>
        {
            var dtos = await response.Content.ReadFromJsonAsync<IssueDto[]>(JsonOptions, cancellationToken);
            return dtos is null
                ? (GitLabFetchResult<IReadOnlyList<GitLabIssue>>)new GitLabFetchResult<IReadOnlyList<GitLabIssue>>.Failed(GitLabFetchFailure.Unreachable, (int)response.StatusCode)
                : new GitLabFetchResult<IReadOnlyList<GitLabIssue>>.Ok(dtos.Select(d => ToIssue(d, project)).ToList());
        }, cancellationToken);
    }

    public async Task<GitLabFetchResult<GitLabFile>> GetFileAsync(
        string project, string filePath, string? gitRef, string token, CancellationToken cancellationToken)
    {
        var uri = $"projects/{Uri.EscapeDataString(project)}/repository/files/{Uri.EscapeDataString(filePath)}/raw";
        if (!string.IsNullOrEmpty(gitRef))
        {
            uri += $"?ref={Uri.EscapeDataString(gitRef)}";
        }

        try
        {
            using var request = BuildRequest(uri, token);
            // Headers first: the size cap must be enforced before the body is
            // buffered, not by downloading a 2 GB artifact and then measuring it.
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (Classify(response.StatusCode) is { } failure)
            {
                return new GitLabFetchResult<GitLabFile>.Failed(failure, (int)response.StatusCode);
            }

            var fileName = Header(response, "X-Gitlab-File-Name");
            var headerPath = Header(response, "X-Gitlab-File-Path");
            var refName = Header(response, "X-Gitlab-Ref") ?? gitRef;
            var contentSha = Header(response, "X-Gitlab-Content-Sha256");
            var lastCommitId = Header(response, "X-Gitlab-Last-Commit-Id");
            var declaredSize = long.TryParse(Header(response, "X-Gitlab-Size"), out var s) ? s
                : response.Content.Headers.ContentLength ?? -1;

            GitLabFile Meta(long size, string? content, GitLabContentIssue? issue) => new(
                project, headerPath ?? filePath, refName, fileName, size,
                contentSha, lastCommitId, content, issue);

            if (declaredSize > options.MaxFileBytes)
            {
                return new GitLabFetchResult<GitLabFile>.Ok(Meta(declaredSize, null, GitLabContentIssue.TooLarge));
            }

            // Trust-but-verify on the declared size: read at most cap + 1 bytes and
            // stop — a response that keeps going past the cap is over it, whatever
            // its headers said.
            var (bytes, overCap) = await ReadCappedAsync(response, options.MaxFileBytes, cancellationToken);
            if (overCap)
            {
                return new GitLabFetchResult<GitLabFile>.Ok(
                    Meta(Math.Max(declaredSize, bytes.Length), null, GitLabContentIssue.TooLarge));
            }

            var size = declaredSize >= 0 ? declaredSize : bytes.Length;
            if (!TryDecodeUtf8Text(bytes, out var text))
            {
                // The wiki embeds text; a binary blob has no sensible inline rendering.
                return new GitLabFetchResult<GitLabFile>.Ok(Meta(size, null, GitLabContentIssue.NotText));
            }

            return new GitLabFetchResult<GitLabFile>.Ok(Meta(size, text, null));
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            return new GitLabFetchResult<GitLabFile>.Failed(GitLabFetchFailure.Unreachable, null);
        }
    }

    // --- shared plumbing ---

    private async Task<GitLabFetchResult<T>> SendAsync<T>(
        string relativeUri, string token,
        Func<HttpResponseMessage, Task<GitLabFetchResult<T>>> onSuccess,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var request = BuildRequest(relativeUri, token);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (Classify(response.StatusCode) is { } failure)
            {
                return new GitLabFetchResult<T>.Failed(failure, (int)response.StatusCode);
            }

            return await onSuccess(response);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            return new GitLabFetchResult<T>.Failed(GitLabFetchFailure.Unreachable, null);
        }
    }

    private HttpRequestMessage BuildRequest(string relativeUri, string token)
    {
        if (httpClient.BaseAddress is null)
        {
            // Unconfigured GitLab never reaches the client (the resolver answers
            // NOT_CONFIGURED first); reaching here is a wiring bug worth a loud throw.
            throw new InvalidOperationException(
                "GitLabHttpClient has no BaseAddress — GitLab:BaseUrl is unset, and the caller should have answered NotConfigured without calling the client.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(httpClient.BaseAddress, relativeUri));
        // Per-request, never a default header — see class doc.
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        return request;
    }

    private static GitLabFetchFailure? Classify(System.Net.HttpStatusCode statusCode) => (int)statusCode switch
    {
        >= 200 and < 300 => null,
        401 => GitLabFetchFailure.InvalidCredential,
        403 or 404 => GitLabFetchFailure.NotFound,
        _ => GitLabFetchFailure.Unreachable,
    };

    /// <summary>Transport-level trouble becomes a typed Unreachable; a cancellation the
    /// CALLER requested propagates — that's the request being abandoned, not GitLab
    /// being down. TaskCanceledException without caller cancellation is the
    /// HttpClient.Timeout firing.</summary>
    private static bool IsTransportFailure(Exception ex, CancellationToken callerToken) => ex switch
    {
        OperationCanceledException when callerToken.IsCancellationRequested => false,
        HttpRequestException or TaskCanceledException or JsonException => true,
        _ => false,
    };

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<(byte[] Bytes, bool OverCap)> ReadCappedAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            if (buffer.Length + read > maxBytes)
            {
                return ([], true);
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static bool TryDecodeUtf8Text(byte[] bytes, out string text)
    {
        try
        {
            // Strict decode (throws on invalid sequences) + NUL scan: the two cheap
            // signals that this is a binary blob rather than source text.
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            if (text.Contains('\0'))
            {
                text = string.Empty;
                return false;
            }

            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static void Append(StringBuilder query, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static GitLabIssue ToIssue(IssueDto dto, string project) => new(
        Project: project,
        Iid: dto.Iid,
        Title: dto.Title ?? string.Empty,
        State: dto.State ?? string.Empty,
        Labels: dto.Labels ?? [],
        AuthorName: dto.Author?.Name,
        AuthorUsername: dto.Author?.Username,
        AssigneeNames: dto.Assignees?.Select(a => a.Name).Where(n => n is not null).Cast<string>().ToList() ?? [],
        WebUrl: dto.WebUrl ?? string.Empty,
        CreatedAtUtc: dto.CreatedAt?.UtcDateTime,
        UpdatedAtUtc: dto.UpdatedAt?.UtcDateTime,
        DueDate: dto.DueDate,
        Milestone: dto.Milestone?.Title,
        Confidential: dto.Confidential);

    // DTOs mirror the REST v4 field names; anything absent stays null rather than
    // failing the parse — GitLab adds fields across versions and the wiki reads a
    // stable subset.
    private sealed record IssueDto(
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("labels")] string[]? Labels,
        [property: JsonPropertyName("author")] UserDto? Author,
        [property: JsonPropertyName("assignees")] UserDto[]? Assignees,
        [property: JsonPropertyName("web_url")] string? WebUrl,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("due_date")] string? DueDate,
        [property: JsonPropertyName("milestone")] MilestoneDto? Milestone,
        [property: JsonPropertyName("confidential")] bool Confidential);

    private sealed record UserDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("username")] string? Username);

    private sealed record MilestoneDto(
        [property: JsonPropertyName("title")] string? Title);
}
