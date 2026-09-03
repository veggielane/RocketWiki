using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §21.15 / §21.4 / §21.12: the additional-selector child table, the prefix
    /// becoming a UK on/off toggle, and the caveat vocabulary becoming the fixed five-eyes
    /// set. The table is ordinary DDL; the two data steps are what a reviewer needs to
    /// read, and each has its own comment at the point it runs.
    ///
    /// <para>A NEW migration rather than an edit to <c>AddPageMarkings</c> or
    /// <c>AddPageMarkingPrefix</c>, both already applied on real SQL Server: editing an
    /// applied migration produces a schema that can never be reproduced from zero.</para>
    ///
    /// <para><b>Ordered before <c>SplitSpaceGrantsIntoAccessAndRole</c></b> (the grant
    /// split), and independent of it; the two are separate files so a rehearsal can stop
    /// between them.</para>
    /// </summary>
    public partial class AddMarkingSelectorsAndFixedCaveat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The selector child table. PK (PageId, Category) — the category, not the
            //    value — is what makes "one value per category on a page" a database fact.
            migrationBuilder.CreateTable(
                name: "PageMarkingSelectors",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageMarkingSelectors", x => new { x.PageId, x.Category });
                    table.ForeignKey(
                        name: "FK_PageMarkingSelectors_PageMarkings_PageId",
                        column: x => x.PageId,
                        principalTable: "PageMarkings",
                        principalColumn: "PageId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageMarkingSelectors_Category_Value_PageId",
                table: "PageMarkingSelectors",
                columns: new[] { "Category", "Value", "PageId" });

            // 2. The prefix becomes a toggle (design.md §21.12): the product now writes
            //    'UK' or NULL and nothing else, so any other value a row still carries —
            //    free text the old mutation accepted — is cleared. Pages and page entries
            //    alike. Presentational, so like AddPageMarkingPrefix's backfill this needs
            //    NO review sweep: the prefix grants nothing and denies nothing, and
            //    clearing it cannot widen or narrow who can read a page.
            migrationBuilder.Sql("UPDATE PageMarkings SET Prefix = NULL WHERE Prefix IS NOT NULL AND Prefix <> 'UK';");
            migrationBuilder.Sql("UPDATE PageEntries SET Prefix = NULL WHERE Prefix IS NOT NULL AND Prefix <> 'UK';");

            // ----------------------------------------------------------------------
            // 3. CAVEAT VOCABULARY — READ THIS BEFORE APPROVING.
            //
            // The eyes-only vocabulary is now the fixed set AUS, CAN, NZ, UK, US
            // (design.md §21.4), on both sides: the marking may only name these, and the
            // principal's nationality claim is canonicalized against them, so a claim
            // value outside the set holds NOTHING and a stored caveat token outside the
            // set matches NOBODY.
            //
            // GB -> UK is remapped here, and only GB. It is the one alias the previous
            // registry-based vocabulary and the dev realm used for the same nation, so
            // rewriting it changes no page's audience: the readers it named (UK
            // nationals) are exactly the readers the new token names. A row that already
            // carried both GB and UK is de-duplicated first, or the rewrite would violate
            // the (PageId, CountryValue) primary key.
            //
            // EVERY OTHER TOKEN OUTSIDE THE SET IS LEFT IN PLACE, ON PURPOSE. This
            // migration cannot know what an instance meant by 'GBR', 'FR' or a
            // site-specific code, and guessing would be a widening nobody reviewed. Such
            // a page fails closed — invisible to everyone, including the audience it
            // names — until an editor re-marks it through setPageMarking, which now
            // refuses anything outside the set. Find them with:
            //
            //   SELECT PageId, CountryValue FROM PageMarkingCountries
            //   WHERE CountryValue NOT IN ('AUS','CAN','NZ','UK','US');
            //   SELECT PageEntryId, CountryValue FROM PageEntryCountries
            //   WHERE CountryValue NOT IN ('AUS','CAN','NZ','UK','US');
            //
            // Run both BEFORE this migration on a copy if the answer matters to a
            // release, and again after: the second run is the list of pages nobody can
            // read until somebody looks at them.
            // ----------------------------------------------------------------------
            migrationBuilder.Sql("""
                DELETE FROM PageMarkingCountries
                WHERE CountryValue = 'GB'
                  AND EXISTS (SELECT 1 FROM PageMarkingCountries u
                              WHERE u.PageId = PageMarkingCountries.PageId AND u.CountryValue = 'UK');
                UPDATE PageMarkingCountries SET CountryValue = 'UK' WHERE CountryValue = 'GB';
                """);
            migrationBuilder.Sql("""
                DELETE FROM PageEntryCountries
                WHERE CountryValue = 'GB'
                  AND EXISTS (SELECT 1 FROM PageEntryCountries u
                              WHERE u.PageEntryId = PageEntryCountries.PageEntryId AND u.CountryValue = 'UK');
                UPDATE PageEntryCountries SET CountryValue = 'UK' WHERE CountryValue = 'GB';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy, and stated as such: the selector rows are dropped with the table,
            // the cleared prefixes are not recoverable (they were free text nothing
            // recorded), and the GB -> UK rewrite is not undone (UK is a legal token of
            // the old registry vocabulary too, so nothing is broken by leaving it).
            migrationBuilder.DropTable(
                name: "PageMarkingSelectors");
        }
    }
}
