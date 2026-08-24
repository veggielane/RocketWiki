using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// data-model.md small-table pattern (KnownGroup / AttributeDefinition / CustomEmoji):
/// GUID v7 PK, a unique natural key, real FK to User with NO ACTION.
///
/// The unique index is on <c>KeyNormalized</c>, NOT on <c>Key</c>, and that is the whole
/// point of the column: SQL Server's default collation is case-insensitive while
/// SQLite's is case-sensitive for ASCII, so indexing the raw key would mean "Owner" and
/// "owner" collide in production and coexist in the SQLite tier — the two test tiers
/// would be enforcing different rules and the looser one would be the one that runs on
/// every commit. Normalizing in the application (PagePropertyKey.Normalize) and indexing
/// the normalized column makes the rule byte-identical on both providers. Contrast
/// CustomEmoji, which gets the same guarantee for free because its grammar admits
/// lowercase only.
///
/// Hard delete, no query filter: the registry is admin-curated vocabulary, not user
/// content — and a key in use cannot be deleted at all (PagePropertyService.DeleteKeyAsync),
/// so there is nothing for a tombstone to protect.
/// </summary>
public class PagePropertyKeyConfiguration : IEntityTypeConfiguration<PagePropertyKey>
{
    public void Configure(EntityTypeBuilder<PagePropertyKey> builder)
    {
        builder.ToTable("PagePropertyKeys");
        builder.HasKey(k => k.Id);

        builder.Property(k => k.Key).HasMaxLength(64).IsRequired();
        builder.Property(k => k.KeyNormalized).HasMaxLength(64).IsRequired();
        builder.Property(k => k.Description).HasMaxLength(256);
        builder.Property(k => k.CreatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(k => k.KeyNormalized)
            .IsUnique()
            .HasDatabaseName("IX_PagePropertyKeys_KeyNormalized");

        // The display order the properties screen and Page.properties both sort by.
        builder.HasIndex(k => new { k.SortOrder, k.Key })
            .HasDatabaseName("IX_PagePropertyKeys_SortOrder_Key");

        // Nullable: a key materialized by a sync import has no local actor (design.md §20).
        builder.HasOne(k => k.CreatedBy)
            .WithMany()
            .HasForeignKey(k => k.CreatedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
