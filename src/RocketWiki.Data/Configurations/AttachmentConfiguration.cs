using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.FileName).HasMaxLength(260).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(127).IsRequired();
        builder.Property(a => a.ContentHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(a => a.StorageKey).HasMaxLength(200).IsRequired();
        builder.Property(a => a.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(a => a.DeletedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(a => a.PageId)
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("IX_Attachments_PageId");

        builder.HasIndex(a => a.ContentHash)
            .HasDatabaseName("IX_Attachments_ContentHash");

        builder.HasOne(a => a.Page)
            .WithMany(p => p.Attachments)
            .HasForeignKey(a => a.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(a => a.UploadedBy)
            .WithMany()
            .HasForeignKey(a => a.UploadedByUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasQueryFilter(a => !a.IsDeleted);
    }
}
