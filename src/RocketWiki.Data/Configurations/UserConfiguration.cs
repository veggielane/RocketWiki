using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Subject).HasMaxLength(255);
        builder.Property(u => u.Email).HasMaxLength(320);
        builder.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
        // See CommentConfiguration for why there's no explicit "nvarchar(max)" here.
        builder.Property(u => u.AttributesJson).IsRequired();
        builder.Property(u => u.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(u => u.LastSeenAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(u => u.Subject)
            .IsUnique()
            .HasFilter("[Subject] IS NOT NULL")
            .HasDatabaseName("IX_Users_Subject");

        builder.HasIndex(u => u.Email)
            .HasDatabaseName("IX_Users_Email");
    }
}
