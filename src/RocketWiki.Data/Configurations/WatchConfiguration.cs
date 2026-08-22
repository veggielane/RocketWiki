using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class WatchConfiguration : IEntityTypeConfiguration<Watch>
{
    public void Configure(EntityTypeBuilder<Watch> builder)
    {
        builder.ToTable("Watches", t => t.HasCheckConstraint(
            "CK_Watches_SpaceXorPage",
            "([SpaceId] IS NOT NULL AND [PageId] IS NULL) OR ([SpaceId] IS NULL AND [PageId] IS NOT NULL)"));

        builder.HasKey(w => w.Id);

        builder.Property(w => w.CreatedAtUtc).HasColumnType("datetime2(3)");

        // data-model.md calls for a single unique (UserId, SpaceId, PageId), but
        // SpaceId/PageId are mutually exclusive (CK_Watches_SpaceXorPage) and both
        // nullable. A plain unique index over all three would let duplicate space
        // watches (or duplicate page watches) through undetected, since SQL Server
        // treats each NULL as distinct within a unique index. Modeled instead as two
        // filtered unique indexes, one per watch kind, which is what "a user can't
        // watch the same space/page twice" actually requires.
        builder.HasIndex(w => new { w.UserId, w.SpaceId }, "IX_Watches_User_Space")
            .IsUnique()
            .HasFilter("[SpaceId] IS NOT NULL");
        builder.HasIndex(w => new { w.UserId, w.PageId }, "IX_Watches_User_Page")
            .IsUnique()
            .HasFilter("[PageId] IS NOT NULL");
        builder.HasIndex(w => w.PageId);
        builder.HasIndex(w => w.SpaceId);

        builder.HasOne(w => w.User)
            .WithMany()
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(w => w.Space)
            .WithMany()
            .HasForeignKey(w => w.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(w => w.Page)
            .WithMany()
            .HasForeignKey(w => w.PageId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
