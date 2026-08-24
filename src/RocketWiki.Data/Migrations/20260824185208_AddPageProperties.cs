using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPageProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PagePropertyKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyNormalized = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagePropertyKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagePropertyKeys_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PageProperties",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PagePropertyKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageProperties", x => new { x.PageId, x.PagePropertyKeyId });
                    table.ForeignKey(
                        name: "FK_PageProperties_PagePropertyKeys_PagePropertyKeyId",
                        column: x => x.PagePropertyKeyId,
                        principalTable: "PagePropertyKeys",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PageProperties_Pages_PageId",
                        column: x => x.PageId,
                        principalTable: "Pages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PageProperties_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageProperties_PagePropertyKeyId_PageId",
                table: "PageProperties",
                columns: new[] { "PagePropertyKeyId", "PageId" });

            migrationBuilder.CreateIndex(
                name: "IX_PageProperties_UpdatedByUserId",
                table: "PageProperties",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PagePropertyKeys_CreatedByUserId",
                table: "PagePropertyKeys",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PagePropertyKeys_KeyNormalized",
                table: "PagePropertyKeys",
                column: "KeyNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PagePropertyKeys_SortOrder_Key",
                table: "PagePropertyKeys",
                columns: new[] { "SortOrder", "Key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageProperties");

            migrationBuilder.DropTable(
                name: "PagePropertyKeys");
        }
    }
}
