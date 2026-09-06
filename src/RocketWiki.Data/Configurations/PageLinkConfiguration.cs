using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// data-model.md: the page link index. Composite PK <c>(SourcePageId, TargetPageId)</c> —
/// "a page links to another at most once" is enforced by the database, not by the
/// indexer's discipline. The <c>TargetPageId</c> index is the backlink lookup ("which
/// pages link here"); the PK already covers the outbound direction.
///
/// <para><b>Source FK cascades; target has no FK at all.</b> Both are decisions, and both
/// are argued on <see cref="PageLink"/> itself so the entity and the schema cannot drift
/// apart. The cascade is the one exception to data-model.md's no-cascade rule: an index
/// row is derived from its page and is nothing without it. The target is unconstrained
/// because a link may name a page that does not exist, and refusing to store that would
/// either fail a legitimate save or lose what the content says; dangling targets are
/// filtered at read time. No navigation on either side — see the entity.</para>
///
/// <para><b>No global query filter</b>, matching PageLabel and PageMarking: a trashed
/// page's rows stay so restoring it restores its links and its backlinks. The read path
/// joins every endpoint to a live page, which is where trashed and archived pages drop
/// out.</para>
/// </summary>
public class PageLinkConfiguration : IEntityTypeConfiguration<PageLink>
{
    public void Configure(EntityTypeBuilder<PageLink> builder)
    {
        builder.ToTable("PageLinks");
        builder.HasKey(l => new { l.SourcePageId, l.TargetPageId });

        builder.HasIndex(l => l.TargetPageId)
            .HasDatabaseName("IX_PageLinks_TargetPageId");

        builder.HasOne<Page>()
            .WithMany()
            .HasForeignKey(l => l.SourcePageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
