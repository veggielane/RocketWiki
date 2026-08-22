using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class PageRevisionConfiguration : IEntityTypeConfiguration<PageRevision>
{
    public void Configure(EntityTypeBuilder<PageRevision> builder)
    {
        builder.ToTable("PageRevisions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Title).HasMaxLength(500).IsRequired();
        // See RocketWiki.Data.Configurations.CommentConfiguration for why there's no
        // explicit "nvarchar(max)" here.
        builder.Property(r => r.Content).IsRequired();
        builder.Property(r => r.EditSummary).HasMaxLength(500);
        builder.Property(r => r.CreatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(r => new { r.PageId, r.RevisionNumber }).IsUnique();

        builder.HasOne(r => r.Page)
            .WithMany(p => p.Revisions)
            .HasForeignKey(r => r.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(r => r.Author)
            .WithMany()
            .HasForeignKey(r => r.AuthorUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
