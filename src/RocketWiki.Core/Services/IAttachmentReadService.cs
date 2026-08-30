using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Services;

/// <summary>design.md §6.7/§10: permission-checked before streaming - see AttachmentDownloadResult for the four possible outcomes and which two must collapse to the same 404.</summary>
public interface IAttachmentReadService
{
    Task<AttachmentDownloadResult> DownloadAsync(Guid attachmentId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorization and metadata with <b>no blob access at all</b> — everything
    /// <see cref="DownloadAsync"/> does except opening the object.
    ///
    /// <para>Exists so a caller can answer a conditional request without paying for bytes
    /// it will throw away. Attachment responses are deliberately <c>no-cache</c> (§10:
    /// every reuse must revalidate, so no read escapes canView and the audit row), which
    /// means a strong ETag match is the COMMON case, not a rare one — and the route was
    /// opening the object first regardless, so each repeat view cost an S3 HEAD plus a
    /// discarded full GET purely to answer 304.</para>
    ///
    /// <para>Splitting this out does not weaken anything: the route still resolves and
    /// audits before it looks at <c>If-None-Match</c>, so a 304 is reached through exactly
    /// the same authorization and the same audit row as a 200 — which is the property that
    /// makes conditional requests safe here at all.</para>
    /// </summary>
    Task<AttachmentAccessResult> ResolveForDownloadAsync(Guid attachmentId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the bytes for an attachment <b>already authorized</b> by
    /// <see cref="ResolveForDownloadAsync"/>. Null means the row exists but the object
    /// does not (design.md §10's operational fault, not an authorization decision).
    /// </summary>
    Task<Stream?> OpenContentAsync(Attachment metadata, CancellationToken cancellationToken = default);
}
