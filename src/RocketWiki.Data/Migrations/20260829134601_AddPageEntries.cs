using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPageEntries : Migration
    {
        /// <summary>
        /// docs/ENTRIES-AND-FORMS-PLAN.md: structured objects stored against a page, each
        /// with its own protective marking.
        ///
        /// <para>Note the BIN2 collation on <c>Collection</c>. It is a lookup string, and
        /// SQL Server's default collation is case-INsensitive while SQLite's is
        /// case-sensitive for ASCII — so without it a normalization bug would behave one
        /// way in production and another in the test tier, silently. Two features have
        /// already been caught by exactly that: the SQL Server blob store's key column and
        /// the page-property key registry.</para>
        ///
        /// <para>The index is filtered on <c>IsDeleted = 0</c> because the only read the
        /// feature performs is "every live entry of one collection on one page", and a
        /// soft-deleted row should cost nothing to skip.</para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Collection = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Data = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageEntryCountries");

            migrationBuilder.DropTable(
                name: "PageEntries");
        }
    }
}
