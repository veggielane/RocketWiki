using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §9.2: the revision an embedding state row's consecutive failures were
    /// counted against, so the job's new attempt ceiling quarantines a revision rather
    /// than a page — editing a page the endpoint keeps rejecting lifts the quarantine.
    ///
    /// Additive, and the 0 default is deliberate rather than merely convenient: existing
    /// rows carry a failure count against an unknown revision, and 0 is a revision no
    /// page has (CurrentRevisionNumber starts at 1). So no page is quarantined by the
    /// upgrade itself — the first failure after it resets the count to 1 and stamps the
    /// real revision. PageEmbeddingState is instance-local and rebuildable (data-model.md),
    /// so nothing here needs to reach the sync bundle format.
    /// </summary>
    public partial class AddEmbeddingFailedRevisionNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedRevisionNumber",
                table: "PageEmbeddingStates",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedRevisionNumber",
                table: "PageEmbeddingStates");
        }
    }
}
