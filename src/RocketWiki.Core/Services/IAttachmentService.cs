using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §10: metadata over IFileStorage. Upload and delete both require canEdit on
/// the target page (an attachment is content, like the page body) and are blocked on
/// replicas, same as every other content mutation.
///
/// Upload order is specified, not a judgement call: write bytes to storage FIRST, then
/// commit the Attachment row and its audit event in one transaction. If the DB commit
/// fails after a successful storage write, the row is never created and the object is
/// orphaned - design.md §10 assigns cleaning that up to a nightly janitor job, not this
/// service, so no compensating delete is attempted here.
///
/// This interface has no dependency on IFileStorage itself (that lives in
/// RocketWiki.Storage) - Core stays free of it; the concrete implementation in
/// RocketWiki.Data is the only piece that knows the storage abstraction exists.
/// </summary>
public interface IAttachmentService
{
    Task<PageMutationResult<Attachment>> UploadAsync(
        UploadAttachmentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Attachment>> DeleteAsync(
        DeleteAttachmentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
