using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// data-model.md (profile pictures): one avatar per user, instance-local, never
/// synced. PK = FK → User, the PageEmbeddingState/GitLabCredential pattern — "one row
/// per user" is a key shape, not a unique-index afterthought. The email-hash columns
/// are fixed-width lowercase-hex ASCII (varchar, not nvarchar) and indexed filtered
/// NOT NULL: they exist solely so the anonymous <c>GET /avatar/{hash}</c> lookup is
/// one seek, and a user with no mirrored email has no probeable row in either index.
/// Non-unique on purpose: <c>User.Email</c> itself is not unique (shadow users can
/// mirror a real user's address), and the lookup resolves ties deterministically.
/// </summary>
public class UserAvatarConfiguration : IEntityTypeConfiguration<UserAvatar>
{
    public void Configure(EntityTypeBuilder<UserAvatar> builder)
    {
        builder.ToTable("UserAvatars");

        builder.HasKey(a => a.UserId);
        builder.Property(a => a.UserId).ValueGeneratedNever();

        builder.Property(a => a.StorageKey).HasMaxLength(200).IsRequired();
        builder.Property(a => a.ContentHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(a => a.EmailHashMd5).HasColumnType("varchar(32)");
        builder.Property(a => a.EmailHashSha256).HasColumnType("varchar(64)");
        builder.Property(a => a.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(a => a.UpdatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(a => a.EmailHashMd5)
            .HasFilter("[EmailHashMd5] IS NOT NULL")
            .HasDatabaseName("IX_UserAvatars_EmailHashMd5");

        builder.HasIndex(a => a.EmailHashSha256)
            .HasFilter("[EmailHashSha256] IS NOT NULL")
            .HasDatabaseName("IX_UserAvatars_EmailHashSha256");

        builder.HasOne(a => a.User)
            .WithOne()
            .HasForeignKey<UserAvatar>(a => a.UserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
