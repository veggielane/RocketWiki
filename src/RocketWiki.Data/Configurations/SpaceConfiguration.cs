using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class SpaceConfiguration : IEntityTypeConfiguration<Space>
{
    public void Configure(EntityTypeBuilder<Space> builder)
    {
        builder.ToTable("Spaces");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Key).HasMaxLength(32).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
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
        // Page/Attachment; applying the same global filter here for consistency even
        // though data-model.md's soft-delete prose only calls out pages/attachments.
        builder.HasQueryFilter(s => !s.IsDeleted);
    }
}
