namespace RocketWiki.Api.GitLab;

/// <summary>
/// design.md §15 (GitLab amendment): <c>GitLab:BaseUrl</c> follows the same
/// fail-closed rule as the OTLP endpoint and <c>VITE_DRAWIO_URL</c> — no default
/// exists, and unset means the feature is absent (every GitLab field answers
/// NOT_CONFIGURED), never a guess at a destination. The URL is where users' GitLab
/// credentials are sent, so a defaulted or fallback value would be a credential
/// exfiltration bug, not a convenience.
///
/// <para>The <c>GitLab</c> feature flag (docs/CONFIGURATION.md "Feature flags") folds in
/// here, at the one seam every GitLab field already reads: with the flag off,
/// <see cref="From"/> discards the configured base URL, so <see cref="IsConfigured"/> is
/// false and every field — the three fetches through their shared gate,
/// <c>gitlabStatus</c>, and the typed client's base address — answers exactly as an
/// instance with no <c>GitLab:BaseUrl</c> does. One seam rather than a check per
/// resolver, and the flag cannot turn anything on: with no URL configured there is
/// nothing for it to keep. Stored tokens are untouched; the settings mutations keep
/// working, as they do when unconfigured.</para>
/// </summary>
public sealed record GitLabOptions(string? BaseUrl, TimeSpan Timeout, long MaxFileBytes)
{
    public const string SectionName = "GitLab";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>REST v4 root, e.g. <c>https://gitlab.example.com/api/v4/</c>. Only valid when <see cref="IsConfigured"/>.</summary>
    public Uri ApiBaseUri => new(BaseUrl!.TrimEnd('/') + "/api/v4/");

    /// <param name="featureEnabled">The <c>GitLab</c> flag (FeatureFlagSnapshot.GitLab). False
    /// yields the unconfigured shape whatever <c>GitLab:BaseUrl</c> says.</param>
    public static GitLabOptions From(IConfiguration configuration, bool featureEnabled) => new(
        BaseUrl: featureEnabled ? configuration["GitLab:BaseUrl"] : null,
        // Short by design: these fetches happen at page-view time, and a page with
        // many embeds must degrade to placeholders, not hang on a slow GitLab.
        Timeout: TimeSpan.FromSeconds(configuration.GetValue("GitLab:TimeoutSeconds", 5)),
        MaxFileBytes: configuration.GetValue("GitLab:MaxFileBytes", 512 * 1024L));
}
