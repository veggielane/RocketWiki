using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropPageEntries : Migration
    {
        /// <summary>
        /// Page entries — structured records stored against a page, with a form fence
        /// on top — are removed. The product decision was to keep the FORM as a future
        /// ticket-creation front end in docs/PLATFORM-PLAN.md and to store nothing of
        /// its own, so the entry store has no successor: nothing is migrated anywhere.
        ///
        /// <para><b>THE DATA IS GONE.</b> Every row in <c>PageEntries</c> and
        /// <c>PageEntryCountries</c> is dropped with its table. Nothing in the product
        /// reads them after this build, and there is no export step: an operator who
        /// wants the records back has the pre-migration backup, and the full-featured
        /// tree is tagged <c>full-feature</c> in the repository if the code to read
        /// them is ever needed. Run this on a copy first if the answer matters to a
        /// release.</para>
        ///
        /// <para>Two earlier migrations also touched these tables and are left exactly
        /// as they were, per the never-edit-an-applied-migration rule:
        /// <c>AddMarkingSelectorsAndFixedCaveat</c> nulled non-UK prefixes on
        /// <c>PageEntries</c> and canonicalized <c>PageEntryCountries</c>, and
        /// <c>AddMarkingUnavailableFlag</c> added <c>PageEntries.IsUnavailable</c>.
        /// Dropping the tables makes those steps moot on the way up — they still run,
        /// against rows that are then discarded — and <c>AddPageEntries</c> itself stays
        /// in the chain so a database migrated from zero passes through the same
        /// history a production one did.</para>
        ///
        /// <para>The outbox is NOT touched. <c>SyncOutboxEvents</c> rows of type
        /// <c>PageEntry</c> (tinyint 11) may remain on a low side that produced them
        /// before upgrading; the enum member is kept as a reserved, retired value and
        /// the importer skips such lines, so those rows still drain and land harmlessly
        /// rather than refusing the bundles around them.</para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageEntryCountries");

            migrationBuilder.DropTable(
                name: "PageEntries");
        }

        /// <summary>
        /// Recreates both tables EMPTY, in the shape they had immediately before this
        /// migration (including the <c>IsUnavailable</c> column and the BIN2 collation
        /// on <c>Collection</c>). Lossy, and stated as such: the rows dropped by
        /// <see cref="Up"/> are not recoverable from here — only from a backup.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Collection = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    IsUnavailable = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageEntries_Pages_PageId",
                        column: x => x.PageId,
                        principalTable: "Pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PageEntries_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PageEntryCountries",
                columns: table => new
                {
                    PageEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountryValue = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageEntryCountries", x => new { x.PageEntryId, x.CountryValue });
                    table.ForeignKey(
                        name: "FK_PageEntryCountries_PageEntries_PageEntryId",
                        column: x => x.PageEntryId,
                        principalTable: "PageEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageEntries_Page_Collection",
                table: "PageEntries",
                columns: new[] { "PageId", "Collection" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PageEntries_UpdatedByUserId",
                table: "PageEntries",
                column: "UpdatedByUserId");
        }
    }
}
