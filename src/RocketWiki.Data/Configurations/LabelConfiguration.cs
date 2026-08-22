using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.ToTable("Labels");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Name).HasMaxLength(100).IsRequired();

        builder.HasIndex(l => new { l.SpaceId, l.Name }).IsUnique();

        builder.HasOne(l => l.Space)
            .WithMany(s => s.Labels)
            .HasForeignKey(l => l.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
