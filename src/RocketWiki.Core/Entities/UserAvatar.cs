namespace RocketWiki.Core.Entities;

/// <summary>
/// One user's uploaded profile picture: metadata + storage key here, bytes in
/// <c>IFileStorage</c> under an <c>avatars/</c> prefix (design.md §10's split, same as
/// <see cref="Attachment"/>). PK = FK → User (the PageEmbeddingState/GitLabCredential
/// pattern): one avatar per user, keyed on identity, not an id to burn.
///
/// Instance-local like <see cref="GitLabCredential"/> and <see cref="Watch"/>: never
/// synced, never exported (no <c>SyncEventType</c>; the outbox classifier has no case
/// for its events), so shadow users on a high instance render initials, never a
/// low-side image.
///
/// <see cref="EmailHashMd5"/>/<see cref="EmailHashSha256"/> are lowercase hex digests
/// of the user's <b>normalized</b> mirrored email (trim + lowercase — the Gravatar
/// normalization), stored so the anonymous Gravatar-protocol endpoint can answer
/// <c>GET /avatar/{hash}</c> without deriving hashes per request. Both digests exist
/// because consumers disagree: historical Gravatar URLs use MD5, the current spec uses
/// SHA-256, and Libravatar accepts either. They mirror <c>User.Email</c>, which JIT
/// provisioning refreshes on every login — the provisioning upsert recomputes these
/// whenever the mirrored email changes, because a stale hash would serve one person's
/// face for another person's address. Null when the user has no mirrored email: such
/// an avatar renders in-wiki but is unreachable by hash, fail closed.
/// </summary>
public class UserAvatar
{
    /// <summary>PK = FK → User: one avatar per user.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Opaque key into IFileStorage (<c>avatars/{yyyy}/{MM}/{guid}</c>).
    /// Replaced (not overwritten) on re-upload; orphaned objects are the §10
    /// janitor's job, same as attachments.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Always the server-produced canonical form — a 512×512 PNG the upload
    /// was decoded, center-cropped, resized, and re-encoded into (never the user's
    /// original bytes); the content type is fixed by that construction, so there is
    /// no ContentType column to disagree with it.</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the PNG bytes — the ETag on both avatar GET routes.</summary>
    public byte[] ContentHash { get; set; } = [];

    /// <summary>MD5 of the normalized email, lowercase hex; null if no email.</summary>
    public string? EmailHashMd5 { get; set; }

    /// <summary>SHA-256 of the normalized email, lowercase hex; null if no email.</summary>
    public string? EmailHashSha256 { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the <b>image</b> last changed. Deliberately not touched by an
    /// email-hash refresh, which changes no content.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
