namespace RocketWiki.Core.Services;

public sealed record SetGitLabTokenRequest(string Token);

/// <summary>The caller-visible facts about their own credential. Never the token —
/// no method on any service returns stored token material to a caller; the single
/// decrypting read path is <see cref="IGitLabCredentialService.GetDecryptedTokenAsync"/>,
/// consumed only by the GitLab fetch path on its way out to GitLab.</summary>
public sealed record GitLabTokenState(bool HasToken);

/// <summary>
/// Per-user GitLab credential storage (design.md GitLab integration section):
/// encrypted at rest via <see cref="ITokenProtector"/>, set/cleared through the
/// domain-event pipeline (audit actions <c>settings.gitlab_token.set</c> /
/// <c>settings.gitlab_token.cleared</c>, committed in the same transaction as the
/// row change), and never readable back through any API surface.
/// </summary>
public interface IGitLabCredentialService
{
    /// <summary>Upserts the caller's token. Validation failures (empty, oversized,
    /// control characters) return <see cref="ValidationError"/>; the error message
    /// never echoes the submitted value.</summary>
    Task<PageMutationResult<GitLabTokenState>> SetTokenAsync(
        SetGitLabTokenRequest request, Guid actingUserId, Events.AuditContext auditContext,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the caller's token. Clearing when none is stored is a
    /// <see cref="ValidationError"/> — there is nothing to audit having cleared.</summary>
    Task<PageMutationResult<GitLabTokenState>> ClearTokenAsync(
        Guid actingUserId, Events.AuditContext auditContext,
        CancellationToken cancellationToken = default);

    Task<bool> HasTokenAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The decrypted token, or null when none is stored or the stored payload no
    /// longer decrypts (<see cref="ITokenProtector.Unprotect"/>). For the GitLab
    /// fetch path only — the value must go straight into an outbound request
    /// header and nowhere else: never a log, a span, an audit row, or a response.
    /// </summary>
    Task<string?> GetDecryptedTokenAsync(Guid userId, CancellationToken cancellationToken = default);
}
