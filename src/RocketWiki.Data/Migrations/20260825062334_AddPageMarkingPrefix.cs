using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §21.12: the national prefix a UK marking is conventionally written with
    /// (<c>UK SECRET</c>). Presentational only — it is not read by the clearance gate,
    /// never appears in a denial reason, and changes no verdict.
    ///
    /// <para>A SECOND migration on top of <c>AddPageMarkings</c> rather than an edit to
    /// it, deliberately: that one is already applied on real SQL Server, and editing an
    /// applied migration produces a schema that can never be reproduced from zero — the
    /// checksum of what ran and what is checked in stop matching, and
    /// <c>Migrate_FromZero_AppliesEveryCheckedInMigration_NothingPending</c> becomes a
    /// test of a fiction.</para>
    /// </summary>
    public partial class AddPageMarkingPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Prefix",
                table: "PageMarkings",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            // Every row that predates the prefix gets the instance default, UK — the
            // whole point of a default is that existing content reads the same way new
            // content does, and a wiki where half the pages render "UK SECRET" and half
            // render "SECRET" would look like a data problem rather than a policy.
            //
            // Unlike AddPageMarkings' backfill, this one carries NO security risk and
            // needs no review sweep: the prefix grants nothing and denies nothing, so
            // asserting UK on a page nobody has looked at cannot widen or narrow who can
            // read it. The column stays NULLABLE — no prefix is a legal marking, and an
            // editor can clear it afterwards.
            migrationBuilder.Sql("UPDATE PageMarkings SET Prefix = 'UK' WHERE Prefix IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Prefix",
                table: "PageMarkings");
        }
    }
}
