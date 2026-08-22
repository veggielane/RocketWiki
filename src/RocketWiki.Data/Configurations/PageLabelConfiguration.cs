using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class PageLabelConfiguration : IEntityTypeConfiguration<PageLabel>
{
    public void Configure(EntityTypeBuilder<PageLabel> builder)
    {
        builder.ToTable("PageLabels");
        builder.HasKey(pl => new { pl.PageId, pl.LabelId });

        builder.HasOne(pl => pl.Page)
            .WithMany(p => p.PageLabels)
            .HasForeignKey(pl => pl.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(pl => pl.Label)
            .WithMany(l => l.PageLabels)
            .HasForeignKey(pl => pl.LabelId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
