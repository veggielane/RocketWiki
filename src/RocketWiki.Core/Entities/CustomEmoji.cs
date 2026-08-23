namespace RocketWiki.Core.Entities;

/// <summary>
/// One named emoji in the instance's admin-curated registry — authors reference it
/// in page/comment Markdown as <c>:name:</c>, which is plain text to the serializer,
/// sync bundles (design.md §12), and the importer (§13); nothing but the SPA ever
/// interprets it. Definitions are <b>instance-local in v1</b>: content carrying
/// <c>:name:</c> syncs as ordinary text and degrades to the literal characters on an
/// instance whose registry lacks the name — the deliberate replica story, mirroring
/// how an unknown group in a synced restriction simply matches nobody (fail closed,
/// except here the closed state is cosmetic, not access-relevant).
///
/// Image bytes live in object storage under an opaque <see cref="StorageKey"/>
/// (design.md §10 — same write-bytes-then-commit order as Attachment, no presigned
/// URLs, always streamed through the API). The stored bytes are always the server's
/// own re-encode of the upload (EmojiImageProcessor): metadata-stripped, square,
/// 32–256 px — never the caller's original bytes.
/// </summary>
public class CustomEmoji
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The registry key authors type between colons. Grammar is
    /// <see cref="Content.EmojiName"/>'s <c>[a-z0-9_-]{1,64}</c> — lowercase only by
    /// construction, so the unique index needs no collation gymnastics and name
    /// matching stays exact/ordinal (the §6.3 instinct applied to content lookups).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"image/png" (static inputs are normalized to PNG) or "image/gif" (animation preserved).</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Size of the stored (re-encoded) bytes, not the upload.</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the stored bytes, 32 bytes. Source of the serve route's ETag
    /// and the GraphQL list's <c>etag</c> field — the SPA's per-name cache key.</summary>
    public byte[] ContentHash { get; set; } = Array.Empty<byte>();

    /// <summary>Opaque key into IFileStorage (<c>emojis/{guid}</c>).</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Stored images are always square (normalization pads/downscales), so one
    /// column carries both dimensions and cannot self-contradict. 32–256 inclusive.</summary>
    public int PixelSize { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
