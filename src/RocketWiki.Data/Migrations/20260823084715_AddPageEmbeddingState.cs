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
    /// TODO(sql-server, container-gated): design.md §9.3 / data-model.md specify
    /// PageEmbeddings.Embedding as SQL Server 2025's native vector(1536) with a DiskANN
    /// cosine index. That DDL is deliberately NOT emitted here — not even as raw
    /// provider-only SQL like InitialCreate's FULLTEXT block — because unlike the FTS
    /// index it would not be additive: converting the column changes what EF's write
    /// path must produce (the model maps a varbinary float blob via a value converter,
    /// and varbinary does not implicitly convert to vector), so shipping
    /// `ALTER TABLE PageEmbeddings ALTER COLUMN Embedding vector(1536)` today would
    /// break the indexer on the one environment (real SQL Server) nothing yet tests.
    /// The follow-up, once the §14 Testcontainers tier exists to prove it, is one
    /// migration + model change together:
    ///   - remap Embedding to Microsoft.Data.SqlClient's SqlVector&lt;float&gt; /
    ///     EF's native vector type mapping (SQL Server provider),
    ///   - ALTER COLUMN to vector(1536) (values re-populate via re-embed — embeddings
    ///     are derived data, rebuildable per §9.4, so a lossy conversion is acceptable),
    ///   - CREATE VECTOR INDEX … WITH (METRIC = 'cosine', TYPE = 'diskann').
    /// Until then, vector search runs the exact-scan fallback on every provider
    /// (SearchService.SearchViaVectorsAsync), which is also what keeps SQLite-tier
    /// tests exercising the real pipeline end to end.
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
