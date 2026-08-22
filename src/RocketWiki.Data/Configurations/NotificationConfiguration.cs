using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedOnAdd();

        builder.Property(n => n.TitleSnapshot).HasMaxLength(500);
        builder.Property(n => n.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(n => n.ReadAtUtc).HasColumnType("datetime2(3)");

        // Two indexes share the same (RecipientUserId, CreatedAtUtc) column list, so
        // the index name must be passed into HasIndex itself - EF Core treats two
        // HasIndex() calls over an identical property list as configuring the same
        // index (last-writer-wins on name/filter) unless disambiguated this way.
        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAtUtc }, "IX_Notifications_Recipient_Unread")
            .HasFilter("[ReadAtUtc] IS NULL");

        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAtUtc }, "IX_Notifications_Recipient_All");

        builder.HasOne(n => n.Recipient)
            .WithMany()
            .HasForeignKey(n => n.RecipientUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(n => n.Page)
            .WithMany()
            .HasForeignKey(n => n.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(n => n.Space)
            .WithMany()
            .HasForeignKey(n => n.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(n => n.Actor)
            .WithMany()
            .HasForeignKey(n => n.ActorUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
