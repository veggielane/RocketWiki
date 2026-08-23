using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Storage;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IAttachmentService. Lives in RocketWiki.Data because it
/// needs both RocketWikiDbContext and IFileStorage (RocketWiki.Storage) - Core stays
/// free of the storage abstraction entirely.
/// </summary>
public class AttachmentService : IAttachmentService
{
    private readonly RocketWikiDbContext _db;
    private readonly IFileStorage _fileStorage;
    private readonly string _localInstanceId;

    public AttachmentService(RocketWikiDbContext db, IFileStorage fileStorage, string localInstanceId)
    {
        _db = db;
        _fileStorage = fileStorage;
        _localInstanceId = localInstanceId;
    }

    public async Task<PageMutationResult<Attachment>> UploadAsync(
        UploadAttachmentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Attachment>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Attachment>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Attachment>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var canEdit = await ComputeCanEditAsync(space, page, principal, cancellationToken);
        if (!canEdit)
        {
            return PageMutationResult<Attachment>.Failure(new ForbiddenError("canEdit required"));
        }

        // Buffer once: the same bytes are hashed (ContentHash, reused by sync bundles
        // to key blobs - design.md §12) and written to storage. Most Stream
        // implementations a caller would pass here aren't seekable twice over.
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        var contentHash = SHA256.HashData(buffer.ToArray());
        buffer.Position = 0;

        var attachment = new Attachment
        {
            PageId = page.Id,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = buffer.Length,
            ContentHash = contentHash,
            StorageKey = $"attachments/{DateTime.UtcNow:yyyy'/'MM}/{Guid.CreateVersion7()}",
            UploadedByUserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };

        // design.md §10: write bytes to storage FIRST, then commit the row + audit
        // event in one transaction. If the DB commit below fails, the object this just
        // wrote is orphaned - a nightly janitor's job to clean up, not this request's.
        await _fileStorage.SaveAsync(attachment.StorageKey, buffer, attachment.ContentType, cancellationToken);

        _db.Attachments.Add(attachment);
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AttachmentAddedEvent(attachment.Id, page.Id, space.Id, space.Key, actingUserId, attachment.FileName));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Attachment>.Success(attachment);
    }

    public async Task<PageMutationResult<Attachment>> DeleteAsync(
        DeleteAttachmentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.Attachments.FirstOrDefaultAsync(a => a.Id == request.AttachmentId, cancellationToken);
        if (attachment is null)
        {
            return PageMutationResult<Attachment>.Failure(new NotFoundError(request.AttachmentId));
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == attachment.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Attachment>.Failure(new NotFoundError(attachment.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Attachment>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Attachment>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var canEdit = await ComputeCanEditAsync(space, page, principal, cancellationToken);
        if (!canEdit)
        {
            return PageMutationResult<Attachment>.Failure(new ForbiddenError("canEdit required"));
        }

        // Soft delete only (data-model.md) - the blob stays in storage, mirroring
        // Page's 30-day trash pattern. A future purge job removes bytes for objects
        // whose row has been gone past retention; not this service's job.
        attachment.IsDeleted = true;
        attachment.DeletedAtUtc = DateTime.UtcNow;
        attachment.DeletedByUserId = actingUserId;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AttachmentDeletedEvent(attachment.Id, page.Id, space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Attachment>.Success(attachment);
    }

    private async Task<bool> ComputeCanEditAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);

        var restrictionIds = page.GetAncestorIds().Append(page.Id).ToArray();
        var restrictions = restrictionIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && restrictionIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        var permission = EffectivePermissionCalculator.Compute(spaceGrants, restrictions, space.IsReplicaOf(_localInstanceId), principal);
        return permission.CanEdit;
    }
}
