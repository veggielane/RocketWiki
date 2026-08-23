using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// Milestone 7 (design.md §9.2/§9.3): per-page embedding bookkeeping for the
    /// background job — see PageEmbeddingState. Derived, instance-local data; never
    /// synced (§9.4).
    ///
    /// The native-vector DDL deliberately not emitted here (it would not have been
    /// additive: the varbinary blob the model then wrote does not convert to vector,
    /// and no tier existed to prove any of it) landed once the §14 Testcontainers tier
    /// could verify it — see AlterPageEmbeddingToNativeVector for the column
    /// conversion, the SqlVector&lt;float&gt; remap, the VECTOR_DISTANCE query path,
    /// and why the DiskANN index specifically remains out of the migration chain
    /// (preview-gated, ≥100-row creation minimum, and read-only-table semantics on
    /// boxed SQL Server 2025). The SQLite tier still stores the blob and runs the
    /// exact-scan fallback, keeping its tests exercising the real pipeline end to end.
    /// </summary>
    public partial class AddPageEmbeddingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageEmbeddingStates",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmbeddedRevisionNumber = table.Column<int>(type: "int", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageEmbeddingStates", x => x.PageId);
                    table.ForeignKey(
                        name: "FK_PageEmbeddingStates_Pages_PageId",
                        column: x => x.PageId,
                        principalTable: "Pages",
                        principalColumn: "Id");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageEmbeddingStates");
        }
    }
}
