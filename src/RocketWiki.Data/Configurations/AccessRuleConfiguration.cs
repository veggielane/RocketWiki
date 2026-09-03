using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// data-model.md / design.md §6.4: one table, three kinds, and the check constraint that
/// makes each kind's column pairing a database fact. Kind 1 is the ROLE grant (SpaceId +
/// Role, and Role may only be Editor (2) or SpaceAdmin (3) — the retired Viewer value (1)
/// is refused, because "viewer" is now an access grant), kind 2 the page restriction,
/// kind 3 the ACCESS grant (SpaceId alone: no role, no action, no page).
/// <c>AccessRuleService.ValidateShape</c> mirrors this text so a bad request is a
/// <c>ValidationError</c> rather than an opaque <c>DbUpdateException</c>; the constraint
/// is what makes the mirror unnecessary for correctness.
/// </summary>
public class AccessRuleConfiguration : IEntityTypeConfiguration<AccessRule>
{
    public const string KindColumnPairingConstraintName = "CK_AccessRules_KindColumnPairing";

    /// <summary>The constraint text, shared with the migration that (re)creates it.</summary>
    public const string KindColumnPairingConstraintSql =
        "(" +
            "[Kind] = 1 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL " +
            "AND [Role] IS NOT NULL AND [Role] IN (2,3) AND [Action] IS NULL" +
        ") OR (" +
            "[Kind] = 2 AND [PageId] IS NOT NULL AND [SpaceId] IS NULL " +
            "AND [Action] IS NOT NULL AND [Role] IS NULL" +
        ") OR (" +
            "[Kind] = 3 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL " +
            "AND [Role] IS NULL AND [Action] IS NULL" +
        ")";

    public void Configure(EntityTypeBuilder<AccessRule> builder)
    {
        builder.ToTable("AccessRules", t => t.HasCheckConstraint(
            KindColumnPairingConstraintName, KindColumnPairingConstraintSql));

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

/// <summary>
/// The selector values an access grant confers (design.md §21.15), one row per
/// (grant, category, value). PK includes the VALUE — a grant may confer APPLE and BANANA
/// both — which is the one shape difference from <c>PageMarkingSelectors</c>, whose PK
/// stops at the category because a page carries one value per category.
///
/// <para>The <c>(Category, Value, AccessRuleId)</c> index is the "which grants confer
/// APPLE" access path, the mirror image of the marking-side index. Both tokens are
/// <c>nvarchar(32)</c> — the catalog's limits — and are validated against the configured
/// catalog by <c>AccessRuleService</c> before a row is written, because SQLite does not
/// enforce declared lengths. NO ACTION on the FK like every other table: a grant's
/// selector rows are removed explicitly by the service that deletes the grant.</para>
/// </summary>
public class AccessRuleSelectorConfiguration : IEntityTypeConfiguration<AccessRuleSelector>
{
    public const int MaxCategoryLength = SelectorCatalog.MaxNameLength;

    public const int MaxValueLength = SelectorCatalog.MaxValueLength;

    public void Configure(EntityTypeBuilder<AccessRuleSelector> builder)
    {
        builder.ToTable("AccessRuleSelectors");
        builder.HasKey(s => new { s.AccessRuleId, s.Category, s.Value });

        builder.Property(s => s.Category).HasMaxLength(MaxCategoryLength).IsRequired();
        builder.Property(s => s.Value).HasMaxLength(MaxValueLength).IsRequired();

        builder.HasIndex(s => new { s.Category, s.Value, s.AccessRuleId })
            .HasDatabaseName("IX_AccessRuleSelectors_Category_Value_AccessRuleId");

        builder.HasOne(s => s.AccessRule)
            .WithMany(r => r.Selectors)
            .HasForeignKey(s => s.AccessRuleId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
