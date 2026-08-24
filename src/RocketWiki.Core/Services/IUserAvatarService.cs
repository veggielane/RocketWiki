using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>The caller-visible fact about their own avatar — the same shape phase 2's
/// SPA reads as <c>hasAvatar</c> on <c>UserRef</c>/<c>CurrentUser</c>.</summary>
public sealed record UserAvatarState(bool HasAvatar);

/// <summary>An already fully-buffered upload (PNG, JPEG, or WebP — see
/// <see cref="IAvatarImageProcessor"/>). Byte[] rather than Stream on purpose: the
/// route has already enforced the size cap, and decoding + hashing both need the
/// whole payload anyway.</summary>
public sealed record SetUserAvatarRequest(byte[] ImageBytes);

/// <summary>
/// The avatar read outcome, mirroring <see cref="AttachmentDownloadResult"/> minus its
/// Denied case: an avatar is display data attached to a user, like the display name a
/// <c>UserRef</c> resolves — there is no per-avatar access rule for a principal to
/// fail, so the only outcomes are found / not found / data-integrity fault.
/// </summary>
public abstract record UserAvatarReadResult
{
    /// <summary><paramref name="ContentHash"/> is the stored SHA-256 of the PNG bytes —
    /// the ETag both GET routes serve, so unchanged avatars revalidate as 304s.</summary>
    public sealed record Found(Stream Content, long SizeBytes, byte[] ContentHash) : UserAvatarReadResult;

    /// <summary>No such user, no avatar row, or (hash lookups) no matching hash.
    /// One case for all of them: the routes answer 404 with nothing to distinguish.</summary>
    public sealed record NotFound : UserAvatarReadResult;

    /// <summary>The row exists but IFileStorage has no matching object — an
    /// operational fault (design.md §10), not an access decision.</summary>
    public sealed record BlobMissing(Guid UserId) : UserAvatarReadResult;
}

/// <summary>
/// Per-user profile pictures (design: profile pictures / Gravatar endpoint).
/// Deliberately self-only on the write side <b>by shape</b>: neither mutation takes a
/// target user — the only user whose avatar can ever be set or cleared is the acting
/// user, so "A sets B's avatar" is unrepresentable rather than merely forbidden.
/// Set/clear flow through the domain-event pipeline (<c>settings.avatar.set</c> /
/// <c>settings.avatar.cleared</c>, committed in the same transaction as the row).
/// Avatars are instance-local: never synced, never exported; shadow users have none.
/// </summary>
public interface IUserAvatarService
{
    /// <summary>Normalizes the upload via <see cref="IAvatarImageProcessor"/>
    /// (decode with limits, center-crop, resize to canonical 512, re-encode to PNG —
    /// only the re-encoded bytes are ever stored), writes those bytes to storage
    /// first, then commits the row + audit event in one transaction (design.md §10's
    /// upload order). Undecodable/oversized/unsupported input is a
    /// <see cref="ValidationError"/>; JANITOR(§10): a replaced image's old object is
    /// orphaned for the §10 janitor, never deleted in-band.</summary>
    Task<PageMutationResult<UserAvatarState>> SetAsync(
        SetUserAvatarRequest request, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the caller's avatar row (JANITOR(§10): the object is the
    /// janitor's, as above). Clearing when none is set is a
    /// <see cref="ValidationError"/>.</summary>
    Task<PageMutationResult<UserAvatarState>> ClearAsync(
        Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default);

    Task<bool> HasAvatarAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The in-wiki read path (any authenticated caller, any user's avatar —
    /// display data, like a display name).</summary>
    Task<UserAvatarReadResult> OpenAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Gravatar-protocol lookup: matches <paramref name="hash"/> — case-folded to
    /// lowercase, so matching is case-insensitive as the protocol expects — against
    /// the stored MD5 (32 hex chars) or SHA-256 (64 hex chars) email hashes. Anything
    /// that is not exactly one of those two shapes is <see cref="UserAvatarReadResult.NotFound"/>
    /// without a query. Whether the endpoint is enabled at all is the route's concern
    /// (<c>Avatars:GravatarEndpointEnabled</c>), not this service's.
    /// </summary>
    Task<UserAvatarReadResult> OpenByEmailHashAsync(string hash, CancellationToken cancellationToken = default);
}
