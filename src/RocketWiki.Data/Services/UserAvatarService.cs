using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Storage;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IUserAvatarService"/>. Lives in RocketWiki.Data for the same
/// reason <see cref="AttachmentService"/> does: it needs both RocketWikiDbContext and
/// IFileStorage. Mirrors the attachment pipeline's storage discipline exactly
/// (design.md §10): bytes to storage FIRST, then row + audit event in one DB
/// transaction; JANITOR(§10): a failed commit orphans the object for the nightly
/// janitor, and a row
/// whose object is missing surfaces as <see cref="UserAvatarReadResult.BlobMissing"/>,
/// not an unhandled 500. No replica check anywhere: an avatar is instance-local user
/// metadata, like a Watch row — not a write into any space's synced content.
/// </summary>
public sealed class UserAvatarService(
    RocketWikiDbContext db, IFileStorage fileStorage, IAvatarImageProcessor imageProcessor) : IUserAvatarService
{
    public async Task<PageMutationResult<UserAvatarState>> SetAsync(
        SetUserAvatarRequest request, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        // Normalize FIRST: decode-with-limits, center-crop, resize, re-encode
        // (IAvatarImageProcessor). Everything after this line - hash, storage, row -
        // sees only the server-produced canonical PNG, never the upload's original
        // bytes. Failures never echo the submitted bytes.
        byte[] canonicalPng;
        switch (imageProcessor.Normalize(request.ImageBytes))
        {
            case AvatarImageResult.Ok ok:
                canonicalPng = ok.Png;
                break;
            case AvatarImageResult.Invalid invalid:
                return PageMutationResult<UserAvatarState>.Failure(new ValidationError(invalid.Message));
            case var other:
                throw new NotSupportedException($"Unhandled {nameof(AvatarImageResult)} case '{other.GetType().Name}'.");
        }

        // The mirrored email the JIT middleware refreshed earlier this same request —
        // the freshest value this instance has (design.md §11.3). Hash drift after
        // later logins is handled by the same middleware (see its email-change hook).
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actingUserId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Acting user {actingUserId} has no local User row - JIT provisioning must run before any mutation.");
        var (md5Hex, sha256Hex) = AvatarEmailHasher.Compute(user.Email);

        var now = DateTime.UtcNow;
        var contentHash = SHA256.HashData(canonicalPng);
        var storageKey = StorageKeys.ForAvatar(now);

        // design.md §10 upload order: storage first, then the row + audit commit
        // together. On re-upload the row points at the NEW key; JANITOR(§10): the old
        // object is orphaned for the janitor rather than deleted in-band, so a failed
        // commit can never leave a row pointing at deleted bytes.
        using (var buffer = new MemoryStream(canonicalPng, writable: false))
        {
            await fileStorage.SaveAsync(storageKey, buffer, "image/png", cancellationToken);
        }

        var avatar = await db.UserAvatars.FirstOrDefaultAsync(a => a.UserId == actingUserId, cancellationToken);
        if (avatar is null)
        {
            avatar = new UserAvatar { UserId = actingUserId, CreatedAtUtc = now };
            db.UserAvatars.Add(avatar);
        }

        avatar.StorageKey = storageKey;
        avatar.SizeBytes = canonicalPng.Length;
        avatar.ContentHash = contentHash;
        avatar.EmailHashMd5 = md5Hex;
        avatar.EmailHashSha256 = sha256Hex;
        avatar.UpdatedAtUtc = now;

        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new AvatarSetEvent(actingUserId));

        await db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<UserAvatarState>.Success(new UserAvatarState(HasAvatar: true));
    }

    public async Task<PageMutationResult<UserAvatarState>> ClearAsync(
        Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var avatar = await db.UserAvatars.FirstOrDefaultAsync(a => a.UserId == actingUserId, cancellationToken);
        if (avatar is null)
        {
            // ValidationError, not NotFoundError, same reasoning as clearing an unset
            // GitLab token: the only id in play is the caller's own.
            return PageMutationResult<UserAvatarState>.Failure(new ValidationError("No avatar is set."));
        }

        var clearedStorageKey = avatar.StorageKey;
        db.UserAvatars.Remove(avatar);

        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new AvatarClearedEvent(actingUserId));

        await db.SaveChangesAsync(cancellationToken);

        // Blob cleanup AFTER the commit, best-effort — the same shape
        // CustomEmojiService.DeleteAsync uses, and for the same reasons: the row and
        // its audit record must not be held hostage to storage availability, and a
        // failed delete leaves an orphan for the janitor rather than data loss.
        //
        // Unlike the RE-upload path above, which deliberately orphans the old object
        // (a failed commit there could otherwise leave a row pointing at deleted
        // bytes), there is no row left here to point at anything. Clearing used to
        // drop the row and leave the object forever, with no reason recorded — an
        // avatar per user, kept indefinitely, that nothing will ever read again.
        try
        {
            await fileStorage.DeleteAsync(clearedStorageKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Orphaned object; JANITOR(§10). Provider exception types differ
            // (IOException vs AmazonS3Exception), hence the broad catch — cancellation
            // still propagates, everything else is the janitor's problem.
            _ = ex;
        }

        return PageMutationResult<UserAvatarState>.Success(new UserAvatarState(HasAvatar: false));
    }

    public Task<bool> HasAvatarAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.UserAvatars.AsNoTracking().AnyAsync(a => a.UserId == userId, cancellationToken);

    public async Task<UserAvatarReadResult> OpenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var avatar = await db.UserAvatars.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        return avatar is null
            ? new UserAvatarReadResult.NotFound()
            : await OpenStoredAsync(avatar, cancellationToken);
    }

    public async Task<UserAvatarReadResult> OpenByEmailHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        // Case-insensitive per the protocol: fold to lowercase, then match exactly
        // against the stored lowercase hex. Only the two digest shapes are queryable
        // at all; anything else is NotFound with no database round trip.
        var normalized = hash.Trim().ToLowerInvariant();
        if (!IsLowerHex(normalized) || normalized.Length is not (32 or 64))
        {
            return new UserAvatarReadResult.NotFound();
        }

        var query = normalized.Length == 32
            ? db.UserAvatars.AsNoTracking().Where(a => a.EmailHashMd5 == normalized)
            : db.UserAvatars.AsNoTracking().Where(a => a.EmailHashSha256 == normalized);

        // Ties are possible (User.Email is not unique); resolve deterministically.
        var avatar = await query.OrderBy(a => a.UserId).FirstOrDefaultAsync(cancellationToken);
        return avatar is null
            ? new UserAvatarReadResult.NotFound()
            : await OpenStoredAsync(avatar, cancellationToken);
    }

    private async Task<UserAvatarReadResult> OpenStoredAsync(UserAvatar avatar, CancellationToken cancellationToken)
    {
        // Open and CATCH, not Exists-then-Open: between the two calls the object can
        // vanish and OpenReadAsync throws FileNotFoundException, which nothing catches —
        // a raw 500 rather than the flagged BlobMissing this branch exists to produce.
        // Every provider converges on FileNotFoundException as the uniform missing
        // signal, so this is race-free AND one round trip cheaper on the hot path.
        try
        {
            var stream = await fileStorage.OpenReadAsync(avatar.StorageKey, cancellationToken);
            return new UserAvatarReadResult.Found(stream, avatar.SizeBytes, avatar.ContentHash);
        }
        catch (FileNotFoundException)
        {
            return new UserAvatarReadResult.BlobMissing(avatar.UserId);
        }
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
