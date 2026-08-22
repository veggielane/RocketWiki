using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class PageConfiguration : IEntityTypeConfiguration<Page>
{
    public void Configure(EntityTypeBuilder<Page> builder)
    {
        builder.ToTable("Pages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.AncestorPath).HasMaxLength(2600).IsRequired();
        builder.Property(p => p.Slug).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Title).HasMaxLength(500).IsRequired();
        // See RocketWiki.Data.Configurations.CommentConfiguration for why there's no
        // explicit "nvarchar(max)" here.
        builder.Property(p => p.CurrentContent).IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(p => p.UpdatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(p => p.DeletedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(p => new { p.SpaceId, p.ParentPageId, p.SortOrder })
            .HasDatabaseName("IX_Pages_Space_Parent_Sort");

        builder.HasIndex(p => new { p.SpaceId, p.ParentPageId, p.Slug })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("IX_Pages_Space_Parent_Slug");

        builder.HasIndex(p => p.AncestorPath)
            .HasDatabaseName("IX_Pages_AncestorPath");

        // Restore looks up "every page sharing this DeleteBatchId" directly; only
        // deleted pages ever have one, so a filtered index keeps it small.
        builder.HasIndex(p => p.DeleteBatchId)
            .HasFilter("[DeleteBatchId] IS NOT NULL")
            .HasDatabaseName("IX_Pages_DeleteBatchId");

        // Full-text index over (Title, CurrentContent) - see the raw SQL at the end of
        // the InitialCreate migration's Up()/Down(), since FTS catalogs/indexes aren't
        // part of the EF relational model. Only ever runs when the migration is applied
        // to real SQL Server; the SQLite test tier builds schema from this model via
        // EnsureCreated() and never touches it, which is exactly why
        // RocketWiki.Data.Services.SearchService has a LIKE fallback (design.md §14).

        builder.HasOne(p => p.Space)
            .WithMany(s => s.Pages)
            .HasForeignKey(p => p.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(p => p.ParentPage)
            .WithMany(p => p.ChildPages)
            .HasForeignKey(p => p.ParentPageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasQueryFilter(p => !p.IsDeleted);
    }
}
