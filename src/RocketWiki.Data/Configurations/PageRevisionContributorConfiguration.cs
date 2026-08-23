using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class PageRevisionContributorConfiguration : IEntityTypeConfiguration<PageRevisionContributor>
{
    public void Configure(EntityTypeBuilder<PageRevisionContributor> builder)
    {
        builder.ToTable("PageRevisionContributors");

        // Composite PK: a user is a contributor of a revision at most once. Rows are
        // immutable and append-only, like the PageRevision they belong to.
        builder.HasKey(c => new { c.PageRevisionId, c.UserId });

        builder.HasOne(c => c.PageRevision)
            .WithMany()
            .HasForeignKey(c => c.PageRevisionId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        // The attribution query this table exists for (design.md §8 co-editing /
        // §7-adjacent): "which revisions did user X contribute to" - the PK covers
        // the per-revision direction.
        builder.HasIndex(c => c.UserId);
    }
}
