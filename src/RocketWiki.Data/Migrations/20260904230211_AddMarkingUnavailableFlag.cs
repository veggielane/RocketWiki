using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §21.10: "we do not know this page's marking" becomes a state the database
    /// can hold. One <c>IsUnavailable</c> bit, NOT NULL, default 0, on <c>PageMarkings</c>
    /// and on <c>PageEntries</c>. The sync importer sets it on the row it writes for a page
    /// or entry that arrives with no usable marking (a format-1/2 bundle, or a malformed
    /// marking); the entity's <c>ToMarking()</c> then returns
    /// <c>ProtectiveMarking.FailClosed</c>, which the marking gate denies to everyone with
    /// <c>marking:unavailable</c>. Any write that states a real marking — a declared
    /// marking arriving through sync, <c>setPageMarking</c>, an entry write — clears it,
    /// because every writer copies the flag from the value it persists and only the
    /// sentinel carries true.
    ///
    /// <para><b>Why now.</b> Until the level stopped gating (§21.12), the importer wrote
    /// that case as a plain TOP SECRET row, and TOP SECRET denied all but the
    /// highest-cleared, so "unknown" and "TOP SECRET" could share a representation. With
    /// the level presentational, a bare TOP SECRET row is readable by everyone the space's
    /// access grants admit, and an unknown marking stored that way was a silent fail-open
    /// on every pre-marking bundle. The flag is read, never inferred from the level: a
    /// page somebody legitimately marked TOP SECRET stays available.</para>
    ///
    /// <para><b>Existing rows are left alone, and that must be stated plainly.</b> A row
    /// the OLDER importer wrote for a no-marking payload is a bare, prefix-less TOP SECRET
    /// (<c>Level = 4</c>, <c>Prefix IS NULL</c>, no countries, no selectors, no actor) — and
    /// so is a genuine TOP SECRET marking an editor set with the UK prefix toggled off
    /// before this migration. The two are byte-identical in the database, so this
    /// migration cannot tell them apart and does NOT backfill the flag; doing so would
    /// lock a legitimately marked page away from everyone, and not doing so leaves an
    /// imported-unknown page readable. The feature is days old and no instance is known to
    /// hold such a row, but an operator inheriting a database from before this migration
    /// should run the review query below, confirm each candidate against its origin
    /// instance's marking, and either re-sync a declared marking for it or set the flag by
    /// hand:
    ///
    /// <code>
    /// SELECT m.PageId, p.SpaceId, p.Slug, m.SetAtUtc
    /// FROM PageMarkings m
    /// JOIN Pages p ON p.Id = m.PageId
    /// WHERE m.IsUnavailable = 0
    ///   AND m.Level = 4              -- TOP_SECRET
    ///   AND m.Prefix IS NULL         -- the sentinel carried no prefix; the product writes UK or none
    ///   AND m.SetByUserId IS NULL    -- applied by sync, not by a local editor
    ///   AND NOT EXISTS (SELECT 1 FROM PageMarkingCountries c WHERE c.PageId = m.PageId)
    ///   AND NOT EXISTS (SELECT 1 FROM PageMarkingSelectors s WHERE s.PageId = m.PageId);
    ///
    /// SELECT e.Id, e.PageId, e.Collection, e.UpdatedAtUtc
    /// FROM PageEntries e
    /// WHERE e.IsUnavailable = 0
    ///   AND e.Level = 4 AND e.Prefix IS NULL AND e.UpdatedByUserId IS NULL
    ///   AND NOT EXISTS (SELECT 1 FROM PageEntryCountries c WHERE c.PageEntryId = e.Id);
    /// </code>
    ///
    /// <para>A candidate that turns out to be a real marking needs nothing; one that turns
    /// out to be an old unknown is repaired with <c>UPDATE PageMarkings SET IsUnavailable =
    /// 1 WHERE PageId = @id</c> (or the entry equivalent) until the origin re-syncs it.
    /// <c>SetByUserId IS NULL</c> is the discriminator most likely to be decisive: the
    /// backstop also writes null, but it never writes TOP SECRET, and the importer is the
    /// only writer that does both.</para>
    ///
    /// <para>Not indexed: the only enforcement read is the per-page PK lookup, and the
    /// review query above is a one-off scan of a table with one row per page.</para>
    /// </summary>
    public partial class AddMarkingUnavailableFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUnavailable",
                table: "PageMarkings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsUnavailable",
                table: "PageEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the column turns every unavailable row back into the bare TOP SECRET
            // the older importer wrote - readable by everyone with space access under the
            // presentational level. Down exists for tooling symmetry; do not run it on a
            // database that holds imported-unknown rows without re-marking them first.
            migrationBuilder.DropColumn(
                name: "IsUnavailable",
                table: "PageMarkings");

            migrationBuilder.DropColumn(
                name: "IsUnavailable",
                table: "PageEntries");
        }
    }
}
