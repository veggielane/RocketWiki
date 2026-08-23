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
    /// SQL Server 2025 native vector(1536) column in production (design.md §9.3),
    /// queried in-engine via VECTOR_DISTANCE; a flat float blob on SQLite. Modeled as
    /// a CLR float[] so Core stays provider-agnostic — the storage split lives in
    /// RocketWiki.Data (RocketWikiDbContext's provider-conditional mapping; dimensions
    /// fixed by PageEmbeddingConfiguration.EmbeddingDimensions, so a model/dimension
    /// change is a migration + full re-embed). The DiskANN index §9.3 names remains
    /// deliberately out of the migration chain — see AlterPageEmbeddingToNativeVector
    /// for the engine-verified reasons.
    /// </summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    /// <summary>Sanity check against config at query time — an index only ever contains one model's vectors.</summary>
    public string Model { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}
