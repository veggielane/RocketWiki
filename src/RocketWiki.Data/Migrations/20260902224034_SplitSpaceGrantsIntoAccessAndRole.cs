using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §6.4: the one grant kind becomes two. Kind 1 stays the ROLE grant (Editor
    /// or SpaceAdmin — it confers what a principal may DO and no visibility); the new kind
    /// 3 is the ACCESS grant (who may SEE the space, carrying the selector values of
    /// §21.15 in the new <c>AccessRuleSelectors</c> child table). "Viewer" is not a role
    /// any more: every Viewer grant becomes an access grant in place, and every Editor /
    /// SpaceAdmin grant gets a MIRROR access grant beside it, so after this migration
    /// every principal can see exactly what they could see before it — behaviour
    /// preserving by construction, not by review.
    ///
    /// <para><b>Ordered after <c>AddMarkingSelectorsAndFixedCaveat</c></b>, and
    /// independent of it. The steps inside <c>Up</c> are ORDER-SENSITIVE and hand-arranged:
    /// the old check constraint forbids kind 3 and forbids a null Role on kind 1, so it
    /// must go before the data moves; the new one forbids Role = 1, so it must come after
    /// the last Viewer row has been converted.</para>
    ///
    /// <para><b>The audit discontinuity, stated plainly (design.md §7).</b> The mirror
    /// rows are inserted by SQL and carry no <c>permission.change</c> audit row of their
    /// own: hand-inserting into the partitioned, composite-keyed audit table from a
    /// migration is exactly the class of operation this design avoids. A replay of the
    /// audit log as-of an instant BEFORE this migration reconstructs the old model
    /// exactly; as-of an instant AFTER it, the mirrored access grants are absent from the
    /// replay — they are derivable 1:1 from the role grants that DO have rows (same space,
    /// same expression, same audit columns), which is why they are copies rather than
    /// something new. Every converted Viewer row keeps its own history: its id, its
    /// expression and its audit columns are untouched, only Kind and Role changed. Find
    /// every migration-written access grant with:
    ///
    /// <code>
    /// SELECT a.Id, a.SpaceId, a.ExpressionJson, a.CreatedAtUtc
    /// FROM AccessRules a
    /// WHERE a.Kind = 3
    ///   AND EXISTS (SELECT 1 FROM AccessRules r
    ///               WHERE r.Kind = 1 AND r.SpaceId = a.SpaceId AND r.ExpressionJson = a.ExpressionJson
    ///                 AND r.CreatedAtUtc = a.CreatedAtUtc);
    /// </code>
    /// </para>
    /// </summary>
    public partial class SplitSpaceGrantsIntoAccessAndRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The selector child table for access grants. PK includes the VALUE: a grant
            //    may confer APPLE and BANANA both (contrast PageMarkingSelectors).
            migrationBuilder.CreateTable(
                name: "AccessRuleSelectors",
                columns: table => new
                {
                    AccessRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRuleSelectors", x => new { x.AccessRuleId, x.Category, x.Value });
                    table.ForeignKey(
                        name: "FK_AccessRuleSelectors_AccessRules_AccessRuleId",
                        column: x => x.AccessRuleId,
                        principalTable: "AccessRules",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRuleSelectors_Category_Value_AccessRuleId",
                table: "AccessRuleSelectors",
                columns: new[] { "Category", "Value", "AccessRuleId" });

            // 2. The OLD pairing constraint must go BEFORE any row moves: it forbids kind 3
            //    outright and forbids a null Role on kind 1.
            migrationBuilder.DropCheckConstraint(
                name: "CK_AccessRules_KindColumnPairing",
                table: "AccessRules");

            // 3. Every Viewer grant becomes an access grant IN PLACE: same id, same
            //    expression, same audit columns, same history. "May see" is exactly what a
            //    viewer grant said, so nothing about its audience changes.
            migrationBuilder.Sql("UPDATE AccessRules SET Kind = 3, Role = NULL WHERE Kind = 1 AND Role = 1;");

            // 4. Every Editor / SpaceAdmin grant gets a MIRROR access grant: same space,
            //    same expression, same audit columns, a fresh id. Roles confer no
            //    visibility after this migration, so without the mirror every editor and
            //    admin would lose sight of their own space. NEWID() and the copied
            //    timestamps are SQL Server SQL, which is fine: migrations only ever run
            //    there (the SQLite tier builds its schema from the model), the
            //    SYSUTCDATETIME() precedent in AddPageMarkings already commits to it, and
            //    the copied CreatedAtUtc is what the class doc's review query keys on.
            migrationBuilder.Sql("""
                INSERT INTO AccessRules (Id, Kind, SpaceId, PageId, Role, Action, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                SELECT NEWID(), 3, SpaceId, NULL, NULL, NULL, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId
                FROM AccessRules
                WHERE Kind = 1 AND Role IN (2, 3);
                """);

            // 5. The NEW pairing constraint, once no row can violate it: kind 1 needs a Role
            //    of 2 or 3 (Viewer's 1 is refused), kind 3 has no Role, Action or PageId.
            migrationBuilder.AddCheckConstraint(
                name: "CK_AccessRules_KindColumnPairing",
                table: "AccessRules",
                sql: "([Kind] = 1 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL AND [Role] IS NOT NULL AND [Role] IN (2,3) AND [Action] IS NULL) OR ([Kind] = 2 AND [PageId] IS NOT NULL AND [SpaceId] IS NULL AND [Action] IS NOT NULL AND [Role] IS NULL) OR ([Kind] = 3 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL AND [Role] IS NULL AND [Action] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order. Lossy, and stated as such: the selector rows are dropped with
            // their table, and an access grant that had no role-grant twin (a Viewer grant
            // converted above, or one created after the split) goes back to being a Viewer
            // grant — the only pre-split row that meant "may see and nothing else".
            migrationBuilder.DropCheckConstraint(
                name: "CK_AccessRules_KindColumnPairing",
                table: "AccessRules");

            // The mirrors first: an access grant with a role-grant sibling of the same
            // space and expression is the copy step 4 made, and the sibling already
            // carries the visibility in the old model.
            migrationBuilder.Sql("""
                DELETE a FROM AccessRules a
                WHERE a.Kind = 3
                  AND EXISTS (SELECT 1 FROM AccessRules r
                              WHERE r.Kind = 1 AND r.SpaceId = a.SpaceId AND r.ExpressionJson = a.ExpressionJson);
                """);
            migrationBuilder.Sql("UPDATE AccessRules SET Kind = 1, Role = 1 WHERE Kind = 3;");

            migrationBuilder.DropTable(
                name: "AccessRuleSelectors");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccessRules_KindColumnPairing",
                table: "AccessRules",
                sql: "([Kind] = 1 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL AND [Role] IS NOT NULL AND [Action] IS NULL) OR ([Kind] = 2 AND [PageId] IS NOT NULL AND [SpaceId] IS NULL AND [Action] IS NOT NULL AND [Role] IS NULL)");
        }
    }
}
