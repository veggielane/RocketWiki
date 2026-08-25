using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// design.md §21 / data-model.md: one protective marking per page. <b>The primary key is
/// the page id</b>, which is the whole enforcement of "1:1" — a second marking for a page
/// is a primary-key violation, not something application code has to prevent. That is
/// deliberate for a table that gates access: "which marking applies" must not be a
/// question with two possible answers.
///
/// <para><c>Level</c> is stored as a tinyint (the enum's own underlying type) rather than
/// a string, so the ordering comparison the whole feature rests on is a numeric one in
/// every provider, and a value outside the four-member ladder cannot be typed in. The
/// wire formats (GraphQL, sync) use the member NAME; only storage is numeric.</para>
///
/// <para><b>No global query filter</b>, matching PageProperty and PageLabel: when a page
/// is soft-deleted its marking row stays, which is what makes restore give back a page
/// with the marking it had. It also means a marking row can outlive nothing — the row is
/// only ever reachable through its page.</para>
/// </summary>
public class PageMarkingConfiguration : IEntityTypeConfiguration<PageMarking>
{
    public void Configure(EntityTypeBuilder<PageMarking> builder)
    {
        builder.ToTable("PageMarkings");
        builder.HasKey(m => m.PageId);

        builder.Property(m => m.Level).HasColumnType("tinyint").IsRequired();
        builder.Property(m => m.SetAtUtc).HasColumnType("datetime2(3)");

        // "Which pages sit at or above level X" is the natural administrative question
        // (a review sweep after the OFFICIAL backfill, §21), and it is the only index
        // this table needs beyond its PK - it is small, one row per page, and every
        // enforcement read is a PK lookup.
        builder.HasIndex(m => m.Level).HasDatabaseName("IX_PageMarkings_Level");

        builder.HasOne(m => m.Page)
            .WithOne(p => p.Marking)
            .HasForeignKey<PageMarking>(m => m.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        // Nullable: a marking applied by a sync import has no local actor (design.md §21),
        // the same shape PageProperty.UpdatedByUserId has.
        builder.HasOne(m => m.SetByUser)
            .WithMany()
            .HasForeignKey(m => m.SetByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>
/// The eyes-only country set, one row per (page, country). A normalized child table
/// rather than a delimited column on PageMarking — see <see cref="PageMarkingCountry"/>
/// for why that matters for an enforcement-critical set.
///
/// <para>The <c>(CountryValue, PageId)</c> index is the "which pages are releasable to
/// X" access path. There is no query surface for it yet, exactly like PageProperty's
/// mirror-image index; the point is that the shape is right now, so adding one later is
/// a resolver, not a migration.</para>
///
/// <para><c>CountryValue</c> is stored canonical (upper-cased, trimmed) and compared
/// ordinally in memory. It is deliberately NOT compared in SQL: SQL Server's default
/// collation is case-insensitive and SQLite's is case-sensitive for ASCII, so a
/// provider-side comparison would enforce two different rules across §14's two tiers —
/// the same trap <c>PagePropertyKey.KeyNormalized</c> exists for. Canonicalizing on
/// write makes the answer byte-identical everywhere.</para>
/// </summary>
public class PageMarkingCountryConfiguration : IEntityTypeConfiguration<PageMarkingCountry>
{
    /// <summary>Long enough for any registered nationality value; short enough to index.</summary>
    public const int MaxCountryValueLength = 32;

    public void Configure(EntityTypeBuilder<PageMarkingCountry> builder)
    {
        builder.ToTable("PageMarkingCountries");
        builder.HasKey(c => new { c.PageId, c.CountryValue });

        builder.Property(c => c.CountryValue).HasMaxLength(MaxCountryValueLength).IsRequired();

        builder.HasIndex(c => new { c.CountryValue, c.PageId })
            .HasDatabaseName("IX_PageMarkingCountries_CountryValue_PageId");

        builder.HasOne(c => c.Marking)
            .WithMany(m => m.Countries)
            .HasForeignKey(c => c.PageId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
