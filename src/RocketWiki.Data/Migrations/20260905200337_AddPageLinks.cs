using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// data-model.md: the page link index — one row per distinct <c>page://{guid}</c> in
    /// a page's live Markdown, keyed <c>(SourcePageId, TargetPageId)</c>, with the
    /// backlink index on <c>TargetPageId</c>. Two schema decisions are argued on the
    /// <c>PageLink</c> entity and only summarised here: the source FK <b>cascades</b> (the
    /// one exception to the no-cascade convention — the row is a derived index of its
    /// page and nothing without it), and the target has <b>no FK</b> (a link may name a
    /// page that does not exist, and refusing to store that would fail a legitimate save
    /// or lose what the content says; dangling targets are filtered at read time).
    ///
    /// <para><b>The backfill indexes every page that already exists</b>, in T-SQL, so the
    /// graph is complete the moment the table exists rather than only for pages saved
    /// afterwards. It is written to produce exactly the rows <c>PageLinkScanner</c> would —
    /// the SQL Server tier pins that row-for-row against <c>PageLink.FromContent</c> over a
    /// corpus of edge cases — because two definitions of "what a page links to" would
    /// diverge one edge case at a time:</para>
    /// <list type="bullet">
    /// <item>The recursive CTE walks every occurrence of <c>page://</c> in the content,
    /// under a <b>binary collation</b> so <c>PAGE://</c> is not a link (the scanner's regex
    /// is case-sensitive on the scheme), and with <c>MAXRECURSION 0</c> because the
    /// default cap of 100 would make a page with 101 links fail the migration.</item>
    /// <item>The 36 characters after each scheme must be a hyphenated GUID in the exact
    /// <c>D</c> layout — the <c>LIKE</c> guard is the regex's character classes spelled
    /// out — before <c>TRY_CONVERT</c> parses them. <c>TRY_CONVERT</c> alone is looser than
    /// the scanner (it accepts a braced form the regex never matches), so the guard, not
    /// the conversion, decides what counts.</item>
    /// <item>Distinct per source with the ordinal assigned in first-occurrence order,
    /// 0-based — the scanner's dedup rule. Upper- and lower-case spellings of one id
    /// collapse to one row, as they do to one <c>Guid</c>.</item>
    /// </list>
    ///
    /// <para><b>Deliberately unfiltered</b>: trashed pages and pages in archived spaces are
    /// indexed too, on the same reasoning as <c>AddSpaceOwner</c>. Restoring a page does
    /// not touch its content, so it does not re-index; a trashed page skipped here would
    /// come back from the trash with no outbound links and no backlinks, silently. The
    /// read path is what hides a trashed page's rows, not their absence.</para>
    /// </summary>
    public partial class AddPageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageLinks",
                columns: table => new
                {
                    SourcePageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetPageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageLinks", x => new { x.SourcePageId, x.TargetPageId });
                    table.ForeignKey(
                        name: "FK_PageLinks_Pages_SourcePageId",
                        column: x => x.SourcePageId,
                        principalTable: "Pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageLinks_TargetPageId",
                table: "PageLinks",
                column: "TargetPageId");

            // Backfill — see the class doc for why each clause is shaped the way it is.
            // The GUID guard is the scanner's regex, [0-9a-fA-F]{8}-{4}-{4}-{4}-{12}, as a
            // LIKE pattern; REPLICATE keeps it legible. Everything string-shaped is compared
            // under the binary collation so the scheme match and the hex ranges mean the
            // same thing here as in .NET.
            migrationBuilder.Sql("""
                DECLARE @Hex nvarchar(16) = N'[0-9a-fA-F]';
                DECLARE @GuidPattern nvarchar(400) =
                    REPLICATE(@Hex, 8) + N'-' + REPLICATE(@Hex, 4) + N'-' + REPLICATE(@Hex, 4) + N'-' + REPLICATE(@Hex, 4) + N'-' + REPLICATE(@Hex, 12);

                WITH Occurrence AS (
                    SELECT p.Id AS SourcePageId,
                           p.CurrentContent AS Content,
                           CHARINDEX(N'page://', p.CurrentContent COLLATE Latin1_General_100_BIN2) AS Position
                    FROM Pages p
                    WHERE CHARINDEX(N'page://', p.CurrentContent COLLATE Latin1_General_100_BIN2) > 0
                    UNION ALL
                    SELECT SourcePageId,
                           Content,
                           CHARINDEX(N'page://', Content COLLATE Latin1_General_100_BIN2, Position + 7)
                    FROM Occurrence
                    WHERE CHARINDEX(N'page://', Content COLLATE Latin1_General_100_BIN2, Position + 7) > 0
                ),
                Token AS (
                    SELECT SourcePageId, Position, SUBSTRING(Content, Position + 7, 36) AS Value
                    FROM Occurrence
                ),
                FirstOccurrence AS (
                    SELECT SourcePageId, TRY_CONVERT(uniqueidentifier, Value) AS TargetPageId, MIN(Position) AS Position
                    FROM Token
                    WHERE Value COLLATE Latin1_General_100_BIN2 LIKE @GuidPattern COLLATE Latin1_General_100_BIN2
                    GROUP BY SourcePageId, TRY_CONVERT(uniqueidentifier, Value)
                )
                INSERT INTO PageLinks (SourcePageId, TargetPageId, Ordinal)
                SELECT SourcePageId,
                       TargetPageId,
                       ROW_NUMBER() OVER (PARTITION BY SourcePageId ORDER BY Position) - 1
                FROM FirstOccurrence
                WHERE TargetPageId IS NOT NULL
                OPTION (MAXRECURSION 0);
                """);
        }

        /// <summary>
        /// Drops the index. Lossless in the only sense that matters: every row is derived
        /// from <c>Pages.CurrentContent</c>, and re-applying <see cref="Up"/> rebuilds it.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageLinks");
        }
    }
}
