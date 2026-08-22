using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class AttributeDefinitionConfiguration : IEntityTypeConfiguration<AttributeDefinition>
{
    public void Configure(EntityTypeBuilder<AttributeDefinition> builder)
    {
        builder.ToTable("AttributeDefinitions");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Key).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ClaimName).HasMaxLength(128).IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(128).IsRequired();
        // See CommentConfiguration for why there's no explicit "nvarchar(max)" here.
        builder.Property(a => a.AllowedValuesJson);

        builder.HasIndex(a => a.Key).IsUnique();
    }
}
