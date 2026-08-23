using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// data-model.md (GitLab integration): one encrypted GitLab PAT per user,
/// instance-local, never synced. PK = FK → User, the PageEmbeddingState pattern —
/// "one row per user" is a key shape, not a unique-index afterthought.
/// </summary>
public class GitLabCredentialConfiguration : IEntityTypeConfiguration<GitLabCredential>
{
    public void Configure(EntityTypeBuilder<GitLabCredential> builder)
    {
        builder.ToTable("GitLabCredentials");

        builder.HasKey(c => c.UserId);

        // Data Protection output for a token-sized secret is a few hundred chars;
        // 1024 is generous headroom without falling into nvarchar(max) for a value
        // that is structurally bounded.
        builder.Property(c => c.ProtectedToken)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(c => c.UpdatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasOne(c => c.User)
            .WithOne()
            .HasForeignKey<GitLabCredential>(c => c.UserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
