using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class SyncSpaceStateConfiguration : IEntityTypeConfiguration<SyncSpaceState>
{
    public void Configure(EntityTypeBuilder<SyncSpaceState> builder)
    {
        builder.ToTable("SyncSpaceStates");
        builder.HasKey(s => new { s.OriginInstanceId, s.SpaceId });

        builder.Property(s => s.OriginInstanceId).HasMaxLength(64);
    }
}
