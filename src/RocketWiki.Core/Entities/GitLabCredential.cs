namespace RocketWiki.Core.Entities;

/// <summary>
/// One user's GitLab personal access token, encrypted at rest — the per-user
/// credential every GitLab fetch runs under (design.md GitLab integration
/// section: a shared service account would surface GitLab content to wiki
/// users GitLab itself would refuse, the exact read-around §6 forbids).
///
/// <see cref="ProtectedToken"/> is ASP.NET Data Protection output
/// (ITokenProtector), never plaintext, and is never exposed through any API
/// surface — the only readers are the credential service handing the
/// decrypted value to the GitLab client for an outbound call. Instance-local
/// like <see cref="Watch"/>: never synced, never exported, no sync outbox
/// classification.
/// </summary>
public class GitLabCredential
{
    /// <summary>PK = FK → User (same pattern as PageEmbeddingState): one credential per user.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Data Protection payload. Opaque; unreadable without this instance's key ring.</summary>
    public string ProtectedToken { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
