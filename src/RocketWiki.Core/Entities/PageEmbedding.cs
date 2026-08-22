namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md / design.md §9.3: PageEmbedding — derived, never synced, rebuilt
/// locally per instance.
/// </summary>
public class PageEmbedding
{
    public long Id { get; set; }
    public Guid PageId { get; set; }
    public Page? Page { get; set; }
    public int ChunkIndex { get; set; }

    /// <summary>For section deep-links.</summary>
    public string HeadingPath { get; set; } = string.Empty;

    /// <summary>SHA-256; skips unchanged chunks on re-embed.</summary>
    public byte[] ChunkHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// SQL Server 2025 native vector(1536) with a DiskANN cosine index in production
    /// (design.md §9.3). Modeled as a CLR float[] so Core stays provider-agnostic; see
    /// RocketWiki.Data's PageEmbeddingConfiguration for the relational mapping and the
    /// TODO covering the native vector column/index.
    /// </summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    /// <summary>Sanity check against config at query time — an index only ever contains one model's vectors.</summary>
    public string Model { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}
