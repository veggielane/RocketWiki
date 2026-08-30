using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.7/§10: a download is one of four outcomes, and NotFound/Denied must be
/// indistinguishable to the caller - "an attachment on a page the caller can't view is
/// absent, not forbidden" is the same leak-prevention rule IPageReadService follows.
/// They are still separate cases here for the same reason ReadResult splits them:
/// §6.7 requires the distinction to survive internally so §7 can audit the denial with
/// its failing restriction, and to die exactly once - at the HTTP boundary, where the
/// route returns the identical 404 for both.
/// The BlobMissing outcome is different in kind: it's a genuine data-integrity fault
/// (the DB row exists and the caller is allowed to see it, but the object store has no
/// matching blob), which design.md §10 says must "surface as a flagged error, not a
/// 500" - exactly because the caller legitimately could view it, there's nothing to
/// hide here.
/// </summary>
public abstract record AttachmentDownloadResult
{
    public sealed record Found(Attachment Metadata, Stream Content) : AttachmentDownloadResult;

    /// <summary>The attachment (or its page, or space) doesn't exist. Collapses to 404 at the route.</summary>
    public sealed record NotFound : AttachmentDownloadResult;

    /// <summary>
    /// The attachment exists but its page fails canView for this principal.
    /// <paramref name="Reason"/> is the rule engine's failing-restriction reason
    /// (<c>restriction:{pageId}:{ruleId}</c> or <c>no-space-role</c>) for the audit row
    /// (design.md §7/§15); <paramref name="AttachmentId"/> is the audit subject. Only
    /// those two - deliberately no Metadata: the route must have nothing it could
    /// accidentally leak about a denied attachment, and the audit row needs nothing more.
    /// Collapses to the exact same 404 as NotFound at the route, after auditing.
    /// </summary>
    public sealed record Denied(Guid AttachmentId, string Reason) : AttachmentDownloadResult;

    /// <summary>The row exists and is viewable, but IFileStorage has no matching object - an operational fault (design.md §10), not an authorization decision.</summary>
    public sealed record BlobMissing(Attachment Metadata) : AttachmentDownloadResult;
}

/// <summary>
/// The outcome of authorizing an attachment download <b>without touching storage</b> —
/// see <see cref="IAttachmentReadService.ResolveForDownloadAsync"/>. Deliberately its own
/// union rather than an <see cref="AttachmentDownloadResult"/> carrying a null or empty
/// stream: a <c>Found</c> whose Content is a placeholder is a trap for the next caller,
/// and there is no BlobMissing case here because nothing has looked at the blob yet.
/// </summary>
public abstract record AttachmentAccessResult
{
    /// <summary>Viewable by this principal. Carries metadata only; bytes come from OpenContentAsync.</summary>
    public sealed record Allowed(Attachment Metadata) : AttachmentAccessResult;

    /// <summary>The attachment (or its page, or space) doesn't exist. Collapses to 404 at the route.</summary>
    public sealed record NotFound : AttachmentAccessResult;

    /// <summary>
    /// Exists but fails canView. Same contract as
    /// <see cref="AttachmentDownloadResult.Denied"/>: the reason is for the audit row
    /// (§7/§15), and there is deliberately no metadata for the route to leak — it
    /// collapses to the identical 404 as NotFound once recorded (§6.7).
    /// </summary>
    public sealed record Denied(Guid AttachmentId, string Reason) : AttachmentAccessResult;
}
