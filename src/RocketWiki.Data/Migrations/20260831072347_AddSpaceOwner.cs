using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// design.md §6.5: every space gains a designated owner — <b>accountability metadata,
    /// never access</b>. Nothing in the rule engine reads this column; see
    /// <c>Space.OwnerUserId</c>.
    ///
    /// <para><b>The backfill makes "every space has an owner" true of existing data</b>, by
    /// seeding each space's owner from its creator. That is the only defensible answer
    /// available to a migration: the creator is the one person the row already records as
    /// having been responsible for it, and any other choice (an arbitrary admin, or nobody)
    /// would either invent accountability that no human agreed to or leave the column
    /// meaningless on every pre-existing row.</para>
    ///
    /// <para><b>It cannot fail on existing data.</b> There is no foreign key here — matching
    /// <c>CreatedByUserId</c> beside it — so the copy is a plain column-to-column UPDATE with
    /// nothing to violate, no join, and no row it can miss. That absence of an FK is load
    /// bearing rather than lax: <c>BundleImportService</c> materialises replica spaces with
    /// <c>CreatedByUserId = Guid.Empty</c> because users do not cross the sync boundary
    /// (§12 — each side runs its own Keycloak), so an FK would make the backfill fail on
    /// exactly the rows a high-side instance holds most of.</para>
    ///
    /// <para>Those replica rows therefore land on <c>Guid.Empty</c>: honestly ownerless, and
    /// resolved as a null <c>Space.owner</c> in the schema rather than a fabricated name. A
    /// high-side admin assigns a real owner through <c>setSpaceOwner</c>, which is why that
    /// mutation has no replica refusal — ownership is local curation under §12, like the
    /// space's name.</para>
    /// </summary>
    public partial class AddSpaceOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Spaces",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Deliberately unfiltered: archived (soft-deleted) spaces are backfilled too,
            // so restoring one does not surface a space with no owner.
            migrationBuilder.Sql("UPDATE Spaces SET OwnerUserId = CreatedByUserId;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Spaces");
        }
    }
}
