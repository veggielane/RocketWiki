using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class KnownGroupConfiguration : IEntityTypeConfiguration<KnownGroup>
{
    public void Configure(EntityTypeBuilder<KnownGroup> builder)
    {
        builder.ToTable("KnownGroups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).HasMaxLength(255).IsRequired();
        builder.Property(g => g.FirstSeenAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(g => g.Name).IsUnique();
    }
}
