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

    /// <summary>
    /// Composed from <see cref="ResolveForDownloadAsync"/> + <see cref="OpenContentAsync"/>
    /// rather than duplicating either, so "authorize, then open" has exactly one
    /// implementation and the two entry points cannot drift into different answers.
    /// </summary>
    public async Task<AttachmentDownloadResult> DownloadAsync(Guid attachmentId, Principal principal, CancellationToken cancellationToken = default)
    {
        var access = await ResolveForDownloadAsync(attachmentId, principal, cancellationToken);

        switch (access)
        {
            case AttachmentAccessResult.NotFound:
                return new AttachmentDownloadResult.NotFound();

            case AttachmentAccessResult.Denied denied:
                return new AttachmentDownloadResult.Denied(denied.AttachmentId, denied.Reason);

            case AttachmentAccessResult.Allowed allowed:
                var content = await OpenContentAsync(allowed.Metadata, cancellationToken);
                return content is null
                    ? new AttachmentDownloadResult.BlobMissing(allowed.Metadata)
                    : new AttachmentDownloadResult.Found(allowed.Metadata, content);

            default:
                // Fail closed on a union member nobody taught this method about.
                return new AttachmentDownloadResult.NotFound();
        }
    }

    public async Task<Stream?> OpenContentAsync(Attachment metadata, CancellationToken cancellationToken = default)
    {
        // Open and CATCH, rather than Exists-then-Open. The pre-check was both a race and
        // a wasted round trip: between the two calls the object can vanish, and
        // OpenReadAsync then throws FileNotFoundException with nothing anywhere in the
        // codebase catching it — a raw, unstructured 500, which is exactly what
        // design.md §10 and the BlobMissing branch exist to prevent. All three providers
        // already converge on FileNotFoundException as the uniform missing signal,
        // deliberately and by test, so catching it is cheaper, race-free, and lands on the
        // same answer the pre-check was reaching for.
        try
        {
            return await _fileStorage.OpenReadAsync(metadata.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<AttachmentAccessResult> ResolveForDownloadAsync(Guid attachmentId, Principal principal, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return new AttachmentAccessResult.NotFound();
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == attachment.PageId, cancellationToken);
        if (page is null)
        {
            return new AttachmentAccessResult.NotFound();
        }

        // Fail closed on an unresolvable space even though the permission computation
        // below no longer needs the row: no space, no grants to hold a role under.
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return new AttachmentAccessResult.NotFound();
        }

        var permission = await ComputePermissionAsync(page, principal, cancellationToken);
        if (!permission.CanView)
        {
            // design.md §6.7/§10: absent, not forbidden - identical to the NotFound
            // case above from the caller's point of view, once the route collapses it.
            // Internally Denied so the route can audit the failing restriction (§7)
            // before returning that identical 404.
            return new AttachmentAccessResult.Denied(
                attachment.Id, permission.ViewDenialReason ?? EffectivePermissionCalculator.NoSpaceAccessReason);
        }

        return new AttachmentAccessResult.Allowed(attachment);
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
