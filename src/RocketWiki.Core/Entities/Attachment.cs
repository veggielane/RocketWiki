namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: Attachment — file metadata; bytes live in object storage (design.md §10).</summary>
public class Attachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PageId { get; set; }
    public Page? Page { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>SHA-256, 32 bytes. Keys sync-bundle blobs, detects duplicates.</summary>
    public byte[] ContentHash { get; set; } = Array.Empty<byte>();

    /// <summary>Opaque key into IFileStorage.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }

    public Guid UploadedByUserId { get; set; }
    public User? UploadedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
