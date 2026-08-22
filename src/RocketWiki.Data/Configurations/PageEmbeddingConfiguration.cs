using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class PageEmbeddingConfiguration : IEntityTypeConfiguration<PageEmbedding>
{
    public void Configure(EntityTypeBuilder<PageEmbedding> builder)
    {
        builder.ToTable("PageEmbeddings");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.HeadingPath).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.ChunkHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(e => e.Model).HasMaxLength(128).IsRequired();
        builder.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2(3)");

        // TODO(sql-server): design.md §9.3 wants a native vector(1536) column with a
        // DiskANN cosine index. EF Core's SQL Server vector-type support is new enough
        // that pinning the exact mapping here would need this instance's precise
        // package versions confirmed. Until that follow-up migration lands, Embedding
        // is stored as a flat little-endian float blob on every provider — this is
        // exactly the "maps Embedding to a blob" behavior data-model.md already
        // specifies for SQLite, just applied uniformly so the column round-trips real
        // data now instead of being dropped. Cosine search runs the in-memory fallback
        // until the native column + DiskANN index exist.
        //
        // No explicit HasColumnType("varbinary(max)"): that's SQL-Server-only syntax
        // and fails schema creation on SQLite (confirmed by running RocketWiki.Data.Tests
        // - its type-name grammar accepts numeric length args but not the keyword MAX).
        // An unbounded byte[] with no HasMaxLength already defaults to varbinary(max) on
        // SQL Server and to a BLOB on SQLite.
        builder.Property(e => e.Embedding)
            .HasConversion(
                v => FloatArrayToBytes(v),
                v => BytesToFloatArray(v))
            .IsRequired();

        builder.HasIndex(e => new { e.PageId, e.ChunkIndex }).IsUnique();

        builder.HasOne(e => e.Page)
            .WithMany()
            .HasForeignKey(e => e.PageId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static byte[] FloatArrayToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloatArray(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
