using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class SyncImportStateConfiguration : IEntityTypeConfiguration<SyncImportState>
{
    public void Configure(EntityTypeBuilder<SyncImportState> builder)
    {
        builder.ToTable("SyncImportStates");
        builder.HasKey(s => s.OriginInstanceId);

        builder.Property(s => s.OriginInstanceId).HasMaxLength(64);
        builder.Property(s => s.LastManifestHash).HasColumnType("char(64)").IsRequired();
        builder.Property(s => s.LastImportAtUtc).HasColumnType("datetime2(3)");
    }
}
