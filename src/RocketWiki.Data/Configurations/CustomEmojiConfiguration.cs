using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// data-model.md small-table pattern (KnownGroup/AttributeDefinition): GUID v7 PK,
/// a unique natural key, real FK to User with NO ACTION. Uniqueness on Name is a
/// plain unique index — case-insensitivity is guaranteed by the grammar (lowercase
/// only, see EmojiName), not by collation, so SQL Server's CI default and SQLite's
/// binary comparison cannot diverge observably. No soft delete: deletion is a hard
/// remove (see CustomEmojiService.DeleteAsync for why), so no query filter either.
/// </summary>
public class CustomEmojiConfiguration : IEntityTypeConfiguration<CustomEmoji>
{
    public void Configure(EntityTypeBuilder<CustomEmoji> builder)
    {
        builder.ToTable("CustomEmojis");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(127).IsRequired();
        builder.Property(e => e.ContentHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(e => e.StorageKey).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CreatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(e => e.Name)
            .IsUnique()
            .HasDatabaseName("IX_CustomEmojis_Name");

        builder.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
