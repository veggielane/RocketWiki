using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPageRevisionContributors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageRevisionContributors",
                columns: table => new
                {
                    PageRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageRevisionContributors", x => new { x.PageRevisionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PageRevisionContributors_PageRevisions_PageRevisionId",
                        column: x => x.PageRevisionId,
                        principalTable: "PageRevisions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PageRevisionContributors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageRevisionContributors_UserId",
                table: "PageRevisionContributors",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageRevisionContributors");
        }
    }
}
