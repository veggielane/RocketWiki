using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// The value rows. Composite PK <c>(PageId, PagePropertyKeyId)</c>, exactly like
/// PageLabel — one value per key per page makes "set" an upsert with no ordering
/// question, and it needs no surrogate id of its own.
///
/// <b>No global query filter</b>, matching PageLabel, with the same consequence stated
/// plainly: when a page is soft-deleted its property rows linger, and any future
/// cross-page report over this table must join to Pages (or apply Page's own filter)
/// rather than assume every row belongs to a live page. That is a deliberate match to
/// the existing per-page metadata table, not an oversight.
///
/// The <c>(PagePropertyKeyId, PageId)</c> index is the "which pages use this key" access
/// path — used today by the delete-key-in-use check, and the index a space-level property
/// report would need, which is why the table is shaped this way now rather than after a
/// migration (design.md §20).
/// </summary>
public class PagePropertyConfiguration : IEntityTypeConfiguration<PageProperty>
{
    public void Configure(EntityTypeBuilder<PageProperty> builder)
    {
        builder.ToTable("PageProperties");
        builder.HasKey(p => new { p.PageId, p.PagePropertyKeyId });

        builder.Property(p => p.Value).HasMaxLength(1000).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(p => new { p.PagePropertyKeyId, p.PageId })
            .HasDatabaseName("IX_PageProperties_PagePropertyKeyId_PageId");

        builder.HasOne(p => p.Page)
            .WithMany(page => page.PageProperties)
            .HasForeignKey(p => p.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(p => p.PropertyKey)
            .WithMany(k => k.PageProperties)
            .HasForeignKey(p => p.PagePropertyKeyId)
            .OnDelete(DeleteBehavior.NoAction);

        // Nullable: a row applied by a sync import has no local actor (design.md §20).
        builder.HasOne(p => p.UpdatedBy)
            .WithMany()
            .HasForeignKey(p => p.UpdatedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
