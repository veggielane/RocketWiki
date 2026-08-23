using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Storage;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IAttachmentReadService. No audit emission here, matching
/// PageReadService's precedent: read-path audit (design.md §7 lists `attachment.download`
/// as an example action) is emitted at the route/resolver layer that owns the actual
/// HTTP streaming response, not in this service - the same boundary that keeps
/// resolvers out of Core/Data scope. What this service does supply (design.md §6.7) is
/// the internal Denied-with-reason outcome that makes that audit possible; the route
/// collapses it to the same 404 as NotFound after recording it.
/// </summary>
public class AttachmentReadService : IAttachmentReadService
{
    private readonly RocketWikiDbContext _db;
    private readonly IFileStorage _fileStorage;

    public AttachmentReadService(RocketWikiDbContext db, IFileStorage fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
    }

    public async Task<AttachmentDownloadResult> DownloadAsync(Guid attachmentId, Principal principal, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return new AttachmentDownloadResult.NotFound();
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == attachment.PageId, cancellationToken);
        if (page is null)
        {
            return new AttachmentDownloadResult.NotFound();
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new AttachmentDownloadResult.NotFound();
        }

        var permission = await ComputePermissionAsync(space, page, principal, cancellationToken);
        if (!permission.CanView)
        {
            // design.md §6.7/§10: absent, not forbidden - identical to the NotFound
            // case above from the caller's point of view, once the route collapses it.
            // Internally Denied so the route can audit the failing restriction (§7)
            // before returning that identical 404.
            return new AttachmentDownloadResult.Denied(
                attachment.Id, permission.ViewDenialReason ?? "no-space-role");
        }

        var blobExists = await _fileStorage.ExistsAsync(attachment.StorageKey, cancellationToken);
        if (!blobExists)
        {
            // design.md §10: a flagged error, not a 500 - the caller legitimately can
            // view this attachment, so there's nothing to hide about its metadata.
            return new AttachmentDownloadResult.BlobMissing(attachment);
        }

        var stream = await _fileStorage.OpenReadAsync(attachment.StorageKey, cancellationToken);
        return new AttachmentDownloadResult.Found(attachment, stream);
    }

    private async Task<EffectivePermission> ComputePermissionAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
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

        return EffectivePermissionCalculator.Compute(spaceGrants, restrictions, isReplicaSpace: false, principal);
    }
}
