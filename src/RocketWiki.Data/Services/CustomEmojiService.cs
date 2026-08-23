using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Storage;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed ICustomEmojiService. Lives in RocketWiki.Data for the same reason
/// AttachmentService does: it needs both RocketWikiDbContext and IFileStorage.
/// No localInstanceId parameter, deliberately — the registry is instance-local
/// (never synced, no replica question), so there is no ownership to verify.
/// </summary>
public class CustomEmojiService : ICustomEmojiService
{
    private readonly RocketWikiDbContext _db;
    private readonly IFileStorage _fileStorage;

    public CustomEmojiService(RocketWikiDbContext db, IFileStorage fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
    }

    public async Task<PageMutationResult<CustomEmoji>> CreateAsync(
        CreateCustomEmojiRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        if (!isInstanceAdmin)
        {
            return PageMutationResult<CustomEmoji>.Failure(new ForbiddenError("instance admin required"));
        }

        if (!EmojiName.IsValid(request.Name))
        {
            return PageMutationResult<CustomEmoji>.Failure(new ValidationError(
                $"Emoji names must match {EmojiName.Pattern} (lowercase letters, digits, '_' and '-'; 1-{EmojiName.MaxLength} characters)."));
        }

        // Exact/ordinal comparison is sufficient for case-insensitive uniqueness:
        // the grammar above admits lowercase only, so no case variant of an existing
        // name can ever get this far - ':BANANA:' is refused as a grammar violation,
        // not treated as a distinct name (see EmojiName's doc).
        if (await _db.CustomEmojis.AnyAsync(e => e.Name == request.Name, cancellationToken))
        {
            return PageMutationResult<CustomEmoji>.Failure(new NameTakenError(request.Name));
        }

        var emoji = new CustomEmoji
        {
            Name = request.Name,
            ContentType = request.ContentType,
            SizeBytes = request.ImageBytes.LongLength,
            ContentHash = SHA256.HashData(request.ImageBytes),
            StorageKey = $"emojis/{Guid.CreateVersion7()}",
            PixelSize = request.PixelSize,
            CreatedByUserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };

        // design.md §10: bytes to storage FIRST, then the row + audit event in one DB
        // transaction. A failed commit orphans the object - the janitor's problem, not
        // this request's; the reverse order could commit a row whose bytes never landed.
        using var content = new MemoryStream(request.ImageBytes, writable: false);
        await _fileStorage.SaveAsync(emoji.StorageKey, content, emoji.ContentType, cancellationToken);

        _db.CustomEmojis.Add(emoji);
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new CustomEmojiCreatedEvent(emoji.Id, emoji.Name, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<CustomEmoji>.Success(emoji);
    }

    public async Task<PageMutationResult<CustomEmoji>> DeleteAsync(
        string name, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        if (!isInstanceAdmin)
        {
            return PageMutationResult<CustomEmoji>.Failure(new ForbiddenError("instance admin required"));
        }

        var emoji = await _db.CustomEmojis.FirstOrDefaultAsync(e => e.Name == name, cancellationToken);
        if (emoji is null)
        {
            // Name-addressed: there is no Guid to report for a name that was never
            // registered. Guid.Empty keeps the shared NotFoundError shape; the route
            // maps the kind, not the id.
            return PageMutationResult<CustomEmoji>.Failure(new NotFoundError(Guid.Empty));
        }

        // Hard delete, deliberately unlike Attachment's 30-day trash: the registry is
        // admin-curated vocabulary, not user content - there is nothing to restore
        // (content keeps its literal ':name:' text and simply stops rendering as an
        // image), and a soft-deleted row would block re-creating the name, which is
        // the one recovery an admin actually wants.
        _db.CustomEmojis.Remove(emoji);
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new CustomEmojiDeletedEvent(emoji.Id, emoji.Name, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);

        // Blob cleanup AFTER the commit, best-effort: the row (and its audit record)
        // must not be held hostage to storage availability, and a failed delete just
        // leaves an orphaned object for the §10 janitor - the same terminal state as
        // a failed upload's orphan.
        try
        {
            await _fileStorage.DeleteAsync(emoji.StorageKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Orphaned object; janitor territory (design.md §10). Provider exception
            // types differ (IOException vs AmazonS3Exception), hence the broad catch -
            // cancellation still propagates, everything else is the janitor's problem.
        }

        return PageMutationResult<CustomEmoji>.Success(emoji);
    }
}
