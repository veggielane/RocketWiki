using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    /// <summary>Exposed so LabelService can refuse an over-long name with a
    /// ValidationError rather than letting it reach the column — SQLite does not enforce
    /// declared lengths, so it stores silently in the test tier and throws
    /// DbUpdateException in production. Confluence allows 255, so a Confluence import is
    /// exactly where this is met.</summary>
    public const int MaxNameLength = 100;

    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.ToTable("Labels");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Name).HasMaxLength(MaxNameLength).IsRequired();

        builder.HasIndex(l => new { l.SpaceId, l.Name }).IsUnique();

        builder.HasOne(l => l.Space)
            .WithMany(s => s.Labels)
            .HasForeignKey(l => l.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
