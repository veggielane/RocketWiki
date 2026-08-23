namespace RocketWiki.Api.GitLab;

/// <summary>
/// design.md §15 (GitLab amendment): <c>GitLab:BaseUrl</c> follows the same
/// fail-closed rule as the OTLP endpoint and <c>VITE_DRAWIO_URL</c> — no default
/// exists, and unset means the feature is absent (every GitLab field answers
/// NOT_CONFIGURED), never a guess at a destination. The URL is where users' GitLab
/// credentials are sent, so a defaulted or fallback value would be a credential
/// exfiltration bug, not a convenience.
/// </summary>
public sealed record GitLabOptions(string? BaseUrl, TimeSpan Timeout, long MaxFileBytes)
{
    public const string SectionName = "GitLab";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>REST v4 root, e.g. <c>https://gitlab.example.com/api/v4/</c>. Only valid when <see cref="IsConfigured"/>.</summary>
    public Uri ApiBaseUri => new(BaseUrl!.TrimEnd('/') + "/api/v4/");

    public static GitLabOptions From(IConfiguration configuration) => new(
        BaseUrl: configuration["GitLab:BaseUrl"],
        // Short by design: these fetches happen at page-view time, and a page with
        // many embeds must degrade to placeholders, not hang on a slow GitLab.
        Timeout: TimeSpan.FromSeconds(configuration.GetValue("GitLab:TimeoutSeconds", 5)),
        MaxFileBytes: configuration.GetValue("GitLab:MaxFileBytes", 512 * 1024L));
}
