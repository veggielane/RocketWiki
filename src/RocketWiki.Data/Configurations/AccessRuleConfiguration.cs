using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class AccessRuleConfiguration : IEntityTypeConfiguration<AccessRule>
{
    public void Configure(EntityTypeBuilder<AccessRule> builder)
    {
        builder.ToTable("AccessRules", t => t.HasCheckConstraint(
            "CK_AccessRules_KindColumnPairing",
            "(" +
                "[Kind] = 1 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL " +
                "AND [Role] IS NOT NULL AND [Action] IS NULL" +
            ") OR (" +
                "[Kind] = 2 AND [PageId] IS NOT NULL AND [SpaceId] IS NULL " +
                "AND [Action] IS NOT NULL AND [Role] IS NULL" +
            ")"));

        builder.HasKey(r => r.Id);

        // See RocketWiki.Data.Configurations.CommentConfiguration for why there's no
        // explicit "nvarchar(max)" here.
        builder.Property(r => r.ExpressionJson).IsRequired();
        builder.Property(r => r.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(r => r.UpdatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(r => r.SpaceId);
        builder.HasIndex(r => r.PageId);

        builder.HasOne(r => r.Space)
            .WithMany(s => s.AccessRules)
            .HasForeignKey(r => r.SpaceId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(r => r.Page)
            .WithMany(p => p.Restrictions)
            .HasForeignKey(r => r.PageId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
