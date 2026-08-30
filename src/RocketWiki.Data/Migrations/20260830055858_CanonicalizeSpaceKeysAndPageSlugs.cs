using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// Folds the two halves of a page address into their canonical forms — <c>Space.Key</c>
    /// upper, <c>Page.Slug</c> lower (SpaceKeys.Canonical / PageSlugs.Canonical) — so that
    /// URLs are case-insensitive. A pure DATA migration: no column changes, because the
    /// schema half landed in BinaryCollationOnStringKeys.
    ///
    /// <para>The two work together and neither is sufficient alone. BIN2 makes the two
    /// providers agree about what "same key" means; canonical storage is what then makes a
    /// case-SENSITIVE unique index enforce case-INsensitive uniqueness, and what stops a
    /// normalized lookup from being ambiguous (with both <c>my-page</c> and <c>My-Page</c>
    /// stored, one URL would address two pages with no defined winner).</para>
    ///
    /// <para><b>It fails loudly rather than corrupting.</b> Folding case can make two rows
    /// collide where they did not before — two live pages in one space whose slugs differ
    /// only in case, or two live spaces whose keys do. Both are near-impossible in practice
    /// (SQL Server's default collation folded them together until BinaryCollationOnStringKeys
    /// shipped, so it refused to store such a pair in the first place; only a database that
    /// spent time between the two migrations could hold one), but "near-impossible" is not
    /// "impossible", and the alternative to checking is a unique-index violation with an
    /// opaque message — or worse, silence. The pre-checks below raise a message naming the
    /// exact problem, and the migration's transaction rolls back with nothing applied.
    /// Resolving one means renaming a page or a space by hand, which is a judgement call an
    /// operator has to make, not something a migration may guess at.</para>
    ///
    /// <para>Deleted rows are folded too, though only live rows are checked for collisions:
    /// the unique indexes are filtered on <c>IsDeleted = 0</c>, so a trashed page's slug
    /// cannot collide with anything — but leaving it mixed-case would mean a restore
    /// silently reintroduces a non-canonical address.</para>
    /// </summary>
    public partial class CanonicalizeSpaceKeysAndPageSlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both checks run before either UPDATE, so a database with a problem in one
            // table is not left with the other half already folded.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM Pages
                    WHERE IsDeleted = 0
                    GROUP BY SpaceId, LOWER(LTRIM(RTRIM(Slug)))
                    HAVING COUNT(*) > 1
                )
                    THROW 50000, 'Cannot canonicalize page slugs: two or more live pages in the same space have slugs that differ only in case or surrounding whitespace. Rename one of each pair, then re-run this migration.', 1;
                """);

            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM Spaces
                    WHERE IsDeleted = 0
                    GROUP BY UPPER(LTRIM(RTRIM([Key])))
                    HAVING COUNT(*) > 1
                )
                    THROW 50000, 'Cannot canonicalize space keys: two or more live spaces have keys that differ only in case or surrounding whitespace. Rename one of each pair, then re-run this migration.', 1;
                """);

            migrationBuilder.Sql(
                "UPDATE Pages SET Slug = LOWER(LTRIM(RTRIM(Slug))) WHERE Slug <> LOWER(LTRIM(RTRIM(Slug)));");
            migrationBuilder.Sql(
                "UPDATE Spaces SET [Key] = UPPER(LTRIM(RTRIM([Key]))) WHERE [Key] <> UPPER(LTRIM(RTRIM([Key])));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. The original casing is not recoverable — it was not
            // recorded anywhere, and inventing one would put a wrong address in the
            // database rather than restore a right one. Reverting this migration leaves
            // the data canonical, which every version of the code reads correctly; only
            // the case-insensitivity of NEW writes goes away with the code.
        }
    }
}
