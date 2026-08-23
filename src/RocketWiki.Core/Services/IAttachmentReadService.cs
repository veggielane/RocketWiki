using RocketWiki.Core.Access;

namespace RocketWiki.Core.Services;

/// <summary>design.md §6.7/§10: permission-checked before streaming - see AttachmentDownloadResult for the four possible outcomes and which two must collapse to the same 404.</summary>
public interface IAttachmentReadService
{
    Task<AttachmentDownloadResult> DownloadAsync(Guid attachmentId, Principal principal, CancellationToken cancellationToken = default);
}
