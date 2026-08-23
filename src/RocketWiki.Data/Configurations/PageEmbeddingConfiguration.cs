using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// Provider-neutral half of the PageEmbedding mapping. The one provider-specific
/// piece — how the Embedding float[] is physically stored — lives in
/// RocketWikiDbContext.OnModelCreating (the SqliteAuditEventIdGenerator precedent):
/// SQL Server maps it to the native vector(1536) column the
/// AlterPageEmbeddingToNativeVector migration created, SQLite keeps the flat
/// little-endian float blob (data-model.md: "On SQLite (tests) this table maps
/// Embedding to a blob and the in-memory cosine fallback handles search").
/// </summary>
public class PageEmbeddingConfiguration : IEntityTypeConfiguration<PageEmbedding>
{
    /// <summary>
    /// data-model.md / design.md §9.3: the vector(1536) column hard-fixes the
    /// embedding dimensions at the schema level — unlike the varbinary blob it
    /// replaced, which would store any length. This constant is the code-side twin of
    /// that DDL: the SQL Server column type in RocketWikiDbContext is built from it,
    /// the API's startup guard (EmbeddingDimensionsStartupCheck) refuses to boot a
    /// SQL Server instance whose configured Ai:Dimensions disagrees with it, and the
    /// SqlServer-tier migration test ties it to sys.columns. Changing the embedding
    /// model to a different dimension count is therefore a migration + full re-embed,
    /// exactly as data-model.md promises — never a config edit.
    /// </summary>
    public const int EmbeddingDimensions = 1536;

    public void Configure(EntityTypeBuilder<PageEmbedding> builder)
    {
        builder.ToTable("PageEmbeddings");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.HeadingPath).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.ChunkHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(e => e.Model).HasMaxLength(128).IsRequired();
        builder.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2(3)");

        // Storage conversion (blob on SQLite, SqlVector<float> on SQL Server) is
        // provider-conditional and applied in RocketWikiDbContext.OnModelCreating —
        // see the class doc. Only the provider-independent facet lives here.
        builder.Property(e => e.Embedding).IsRequired();

        builder.HasIndex(e => new { e.PageId, e.ChunkIndex }).IsUnique();

        builder.HasOne(e => e.Page)
            .WithMany()
            .HasForeignKey(e => e.PageId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    /// <summary>SQLite storage shape: flat little-endian float32 blob. Public statics so the
    /// context's provider-conditional mapping (and tests) share one definition.</summary>
    public static byte[] FloatArrayToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] BytesToFloatArray(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
