using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class PageEmbeddingStateConfiguration : IEntityTypeConfiguration<PageEmbeddingState>
{
    public void Configure(EntityTypeBuilder<PageEmbeddingState> builder)
    {
        builder.ToTable("PageEmbeddingStates");

        // PK = FK: exactly one state row per page, no identity column to burn.
        builder.HasKey(e => e.PageId);
        builder.Property(e => e.PageId).ValueGeneratedNever();

        builder.Property(e => e.LastAttemptAtUtc).HasColumnType("datetime2(3)");
        builder.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2(3)");

        // data-model.md "No cascade deletes": page deletion is soft (trash) anyway; the
        // embedding job purges state + chunk rows for trashed pages explicitly.
        builder.HasOne(e => e.Page)
            .WithOne()
            .HasForeignKey<PageEmbeddingState>(e => e.PageId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
