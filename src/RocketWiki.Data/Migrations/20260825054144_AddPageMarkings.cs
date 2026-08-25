using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §21: protective markings. Creates the two tables AND backfills every
    /// existing page — see the backfill's own comment at the bottom of Up(), which is the
    /// part of this migration a reviewer needs to read.
    /// </summary>
    public partial class AddPageMarkings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageMarkings",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false),
                    SetAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SetByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageMarkings", x => x.PageId);
                    table.ForeignKey(
                        name: "FK_PageMarkings_Pages_PageId",
                        column: x => x.PageId,
                        principalTable: "Pages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PageMarkings_Users_SetByUserId",
                        column: x => x.SetByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PageMarkingCountries",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountryValue = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageMarkingCountries", x => new { x.PageId, x.CountryValue });
                    table.ForeignKey(
                        name: "FK_PageMarkingCountries_PageMarkings_PageId",
                        column: x => x.PageId,
                        principalTable: "PageMarkings",
                        principalColumn: "PageId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageMarkingCountries_CountryValue_PageId",
                table: "PageMarkingCountries",
                columns: new[] { "CountryValue", "PageId" });

            migrationBuilder.CreateIndex(
                name: "IX_PageMarkings_Level",
                table: "PageMarkings",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_PageMarkings_SetByUserId",
                table: "PageMarkings",
                column: "SetByUserId");

            // ----------------------------------------------------------------------
            // BACKFILL — READ THIS BEFORE APPROVING.
            //
            // Every page that existed before protective markings shipped is stamped
            // OFFICIAL (Level = 1), with no eyes-only caveat and no actor.
            //
            // THIS IS THE LOWEST LEVEL IN THE SCHEME. It is the PRAGMATIC choice, NOT
            // the safe-by-default one, and it is deliberately not disguised as one:
            //
            //   * Nobody has reviewed this content. The wiki is asserting OFFICIAL on
            //     its behalf because the alternative is worse, not because it knows.
            //   * If any pre-existing page is in fact SECRET or above, it is now
            //     readable by every user who could already read it - which is exactly
            //     the state it was in one migration ago, but it is now WEARING A
            //     MARKING THAT SAYS IT IS FINE. That is the real risk here: the
            //     marking looks like a reviewed judgement and is not one.
            //   * EXISTING CONTENT MUST BE REVIEWED AND RE-MARKED. Query
            //     PageMarkings for SetByUserId IS NULL and SetAtUtc = this migration's
            //     run time to find every page nobody has yet looked at.
            //
            // Backfilling to TOP SECRET instead was considered and rejected, once,
            // with reasons: it would make every page in the instance invisible to
            // everyone below TOP SECRET clearance the moment the migration ran - the
            // wiki would lock itself out of itself, including out of the admin pages
            // describing how to fix it, and including on instances where nobody has a
            // clearance claim configured at all (who resolve to OFFICIAL by §21's
            // fail-closed default). The recovery would be a hand-written UPDATE
            // against a production database, which is the one operation this whole
            // design exists to avoid. An access control that has to be switched off to
            // be adopted does not get adopted.
            //
            // Note the asymmetry with the RUNTIME rule, which is deliberate: a page
            // found at runtime with no marking row reads as TOP SECRET, because at
            // runtime a missing row means something is broken. Here a missing row means
            // the feature did not exist yet, which is a different fact and gets a
            // different answer.
            // ----------------------------------------------------------------------
            migrationBuilder.Sql("""
                INSERT INTO PageMarkings (PageId, Level, SetAtUtc, SetByUserId)
                SELECT p.Id, 1, SYSUTCDATETIME(), NULL
                FROM Pages p
                WHERE NOT EXISTS (SELECT 1 FROM PageMarkings m WHERE m.PageId = p.Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageMarkingCountries");

            migrationBuilder.DropTable(
                name: "PageMarkings");
        }
    }
}
