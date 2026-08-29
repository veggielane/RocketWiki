using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

/// <summary>
/// docs/ENTRIES-AND-FORMS-PLAN.md: structured objects stored against a page, each with its
/// own protective marking.
///
/// <para><c>Level</c> is a tinyint (the enum's underlying type) for the same reason
/// <see cref="PageMarkingConfiguration"/> makes that choice: the ordering comparison the
/// gate rests on must be numeric in every provider, and a value outside the four-member
/// ladder cannot be typed in. Wire formats use the member NAME; only storage is numeric.</para>
///
/// <para><b>No global query filter on the page's deletion</b>, matching PageProperty,
/// PageLabel and PageMarking: when a page is trashed its entries stay, which is what makes
/// restore give back a page with the entries it had. The entry's OWN <c>IsDeleted</c> is
/// filtered, because that one means "this entry was deleted" rather than "its page was".</para>
/// </summary>
public class PageEntryConfiguration : IEntityTypeConfiguration<PageEntry>
{
    /// <summary>Long enough for any form name anyone writes; short enough that the column
    /// is never where a sentence ends up.</summary>
    public const int MaxCollectionLength = 64;

    /// <summary>64 KiB. An entry is structured metadata, not a document — anything larger
    /// is an attachment, which has its own storage, quota and streaming story.</summary>
    public const int MaxDataLength = 64 * 1024;

    public void Configure(EntityTypeBuilder<PageEntry> builder)
    {
        builder.ToTable("PageEntries");
        builder.HasKey(e => e.Id);

        // The binary collation this column needs is applied in RocketWikiDbContext, not
        // here: `Latin1_General_100_BIN2` is a SQL Server collation name and SQLite has
        // never heard of it, so setting it unconditionally makes every SQLite test fail
        // at context construction. See the provider branch there, and SqlServerFileStorage
        // for the same lesson learned the same way.
        builder.Property(e => e.Collection)
            .HasMaxLength(MaxCollectionLength)
            .IsRequired();

        builder.Property(e => e.Data).HasMaxLength(MaxDataLength).IsRequired();
        builder.Property(e => e.Version).IsRequired();
        builder.Property(e => e.Level).HasColumnType("tinyint").IsRequired();
        builder.Property(e => e.Prefix).HasMaxLength(PageMarkingConfiguration.MaxPrefixLength);
        builder.Property(e => e.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2(3)");

        // The only read pattern v1 needs: every live entry of one collection on one page.
        // Filtered on IsDeleted so a deleted entry costs nothing to skip.
        builder.HasIndex(e => new { e.PageId, e.Collection })
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("IX_PageEntries_Page_Collection");

        builder.HasQueryFilter(e => !e.IsDeleted);

        builder.HasOne(e => e.Page)
            .WithMany()
            .HasForeignKey(e => e.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.UpdatedByUser)
            .WithMany()
            .HasForeignKey(e => e.UpdatedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>One country in an entry's eyes-only set — see PageMarkingCountryConfiguration
/// for why the set is a table.</summary>
public class PageEntryCountryConfiguration : IEntityTypeConfiguration<PageEntryCountry>
{
    /// <summary>Matches the nationality attribute's allowed-value length.</summary>
    public const int MaxCountryLength = 32;

    public void Configure(EntityTypeBuilder<PageEntryCountry> builder)
    {
        builder.ToTable("PageEntryCountries");
        builder.HasKey(c => new { c.PageEntryId, c.CountryValue });

        builder.Property(c => c.CountryValue).HasMaxLength(MaxCountryLength).IsRequired();

        builder.HasOne(c => c.PageEntry)
            .WithMany(e => e.Countries)
            .HasForeignKey(c => c.PageEntryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
