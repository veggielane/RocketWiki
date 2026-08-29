using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpaceUniquePageSlug : Migration
    {
        /// <summary>
        /// Widens page-slug uniqueness from (space, parent) to (space), because the slug
        /// became a page's address — /spaces/{key}/{slug}, with the hierarchy left out so
        /// a page keeps its URL when it moves. An address has to be unique over the thing
        /// it addresses.
        ///
        /// <para>ON EXISTING DATA THIS CAN FAIL, and should. Any space holding two live
        /// pages that share a slug under different parents was legal before and is not
        /// now; creating the unique index will raise error 1505 naming the duplicate. The
        /// fix is to rename one of them first — which is a decision about someone's
        /// content, so this migration deliberately does not guess at it. Find them with:
        /// <code>
        /// SELECT SpaceId, Slug, COUNT(*) FROM Pages WHERE IsDeleted = 0
        /// GROUP BY SpaceId, Slug HAVING COUNT(*) > 1;
        /// </code>
        /// </para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pages_Space_Parent_Slug",
                table: "Pages");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Space_Slug",
                table: "Pages",
                columns: new[] { "SpaceId", "Slug" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pages_Space_Slug",
                table: "Pages");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Space_Parent_Slug",
                table: "Pages",
                columns: new[] { "SpaceId", "ParentPageId", "Slug" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }
    }
}
