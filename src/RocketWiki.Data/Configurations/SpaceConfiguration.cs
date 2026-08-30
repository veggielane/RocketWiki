using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class SpaceConfiguration : IEntityTypeConfiguration<Space>
{
    /// <summary>Exposed so SpaceService can refuse an over-long key with a ValidationError
    /// rather than letting it reach the column. SQLite does not enforce declared lengths,
    /// so an unchecked one stores silently in the test tier and throws DbUpdateException in
    /// production — the same tier-parity reason PageMarkingConfiguration exposes
    /// MaxPrefixLength. Confluence personal-space keys (<c>~accountId</c>) routinely exceed
    /// this, so the importer hits it on real data.</summary>
    public const int MaxKeyLength = 32;

    /// <inheritdoc cref="MaxKeyLength"/>
    public const int MaxNameLength = 200;
    public void Configure(EntityTypeBuilder<Space> builder)
    {
        builder.ToTable("Spaces");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Key).HasMaxLength(MaxKeyLength).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(MaxNameLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.Property(s => s.OriginInstanceId).HasMaxLength(64).IsRequired();
        builder.Property(s => s.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(s => s.DeletedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(s => s.Key)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("IX_Spaces_Key");

        // Circular FK: HomepageId -> Page.Id, Page.SpaceId -> Space.Id. Safe because
        // HomepageId is nullable and set only after the first page exists (data-model.md).
        builder.HasOne(s => s.Homepage)
            .WithMany()
            .HasForeignKey(s => s.HomepageId)
            .OnDelete(DeleteBehavior.NoAction);

        // Space carries the same IsDeleted/DeletedAtUtc/DeletedByUserId columns as
        // Page/Attachment; data-model.md's soft-delete convention names spaces
        // alongside pages and attachments, so the same global filter applies here.
        builder.HasQueryFilter(s => !s.IsDeleted);
    }
}
