using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;
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
    private readonly PermissionContextLoader _permissions;

    public AttachmentReadService(RocketWikiDbContext db, IFileStorage fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
        _permissions = new PermissionContextLoader(db);
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

        // Fail closed on an unresolvable space even though the permission computation
        // below no longer needs the row: no space, no grants to hold a role under.
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new AttachmentDownloadResult.NotFound();
        }

        var permission = await ComputePermissionAsync(page, principal, cancellationToken);
        if (!permission.CanView)
        {
            // design.md §6.7/§10: absent, not forbidden - identical to the NotFound
            // case above from the caller's point of view, once the route collapses it.
            // Internally Denied so the route can audit the failing restriction (§7)
            // before returning that identical 404.
            return new AttachmentDownloadResult.Denied(
                attachment.Id, permission.ViewDenialReason ?? "no-space-role");
        }

        // Open and CATCH, rather than Exists-then-Open. The pre-check was both a race and
        // a wasted round trip: between the two calls the object can vanish, and
        // OpenReadAsync then throws FileNotFoundException with nothing anywhere in the
        // codebase catching it — a raw, unstructured 500, which is exactly what
        // design.md §10 and this BlobMissing branch exist to prevent. All three providers
        // already converge on FileNotFoundException as the uniform missing signal,
        // deliberately and by test, so catching it is cheaper, race-free, and lands on the
        // same answer the pre-check was reaching for.
        try
        {
            var stream = await _fileStorage.OpenReadAsync(attachment.StorageKey, cancellationToken);
            return new AttachmentDownloadResult.Found(attachment, stream);
        }
        catch (FileNotFoundException)
        {
            // design.md §10: a flagged error, not a 500 - the caller legitimately can
            // view this attachment, so there's nothing to hide about its metadata.
            return new AttachmentDownloadResult.BlobMissing(attachment);
        }
    }

    /// <summary>
    /// Replica status is irrelevant to canView (design.md §6.4: it only ever suppresses
    /// canEdit), which is the only verdict a download gate reads — hence false rather
    /// than this service carrying a local instance id it has no other use for.
    /// </summary>
    private async Task<EffectivePermission> ComputePermissionAsync(Page page, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(page, isReplicaSpace: false, cancellationToken);
        return context.Compute(principal);
    }
}
