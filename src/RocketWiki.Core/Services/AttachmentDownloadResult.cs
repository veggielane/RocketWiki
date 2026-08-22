using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.7/§10: a download is one of three outcomes, and the first two must be
/// indistinguishable to the caller - "an attachment on a page the caller can't view is
/// absent, not forbidden" is the same leak-prevention rule IPageReadService follows.
/// The third outcome is different in kind: it's a genuine data-integrity fault (the DB
/// row exists and the caller is allowed to see it, but the object store has no matching
/// blob), which design.md §10 says must "surface as a flagged error, not a 500" -
/// exactly because the caller legitimately could view it, there's nothing to hide here.
/// </summary>
public abstract record AttachmentDownloadResult
{
    public sealed record Found(Attachment Metadata, Stream Content) : AttachmentDownloadResult;

    /// <summary>The attachment doesn't exist, or its page isn't viewable - deliberately fused into one outcome.</summary>
    public sealed record NotFound : AttachmentDownloadResult;

    /// <summary>The row exists and is viewable, but IFileStorage has no matching object - an operational fault (design.md §10), not an authorization decision.</summary>
    public sealed record BlobMissing(Attachment Metadata) : AttachmentDownloadResult;
}
