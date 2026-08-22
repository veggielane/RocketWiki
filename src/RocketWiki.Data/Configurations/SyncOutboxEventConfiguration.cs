using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class SyncOutboxEventConfiguration : IEntityTypeConfiguration<SyncOutboxEvent>
{
    public void Configure(EntityTypeBuilder<SyncOutboxEvent> builder)
    {
        builder.ToTable("SyncOutboxEvents");
        builder.HasKey(e => e.Id);

        // See CommentConfiguration for why there's no explicit "nvarchar(max)" here.
        builder.Property(e => e.PayloadJson).IsRequired();
        builder.Property(e => e.CreatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(e => new { e.SpaceId, e.SequenceNumber }).IsUnique();

        builder.HasOne(e => e.Space)
            .WithMany()
            .HasForeignKey(e => e.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
