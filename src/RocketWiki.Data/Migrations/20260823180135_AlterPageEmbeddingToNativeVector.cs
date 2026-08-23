using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <summary>
    /// REVIEW CLOSELY — this migration DELETES ALL ROWS in PageEmbeddings and
    /// PageEmbeddingStates (derived data only; see "Why truncate" below).
    ///
    /// The follow-up the AddPageEmbeddingState migration promised, landed as one unit
    /// now that the §14 SQL Server Testcontainers tier exists to prove it:
    /// PageEmbeddings.Embedding becomes SQL Server 2025's native vector(1536) column
    /// (design.md §9.3 / data-model.md), the model maps it via Microsoft.Data.SqlClient's
    /// SqlVector&lt;float&gt; (RocketWikiDbContext.OnModelCreating, SQL Server branch),
    /// and SearchService's SQL Server branch scores candidates in-engine with
    /// VECTOR_DISTANCE('cosine', …) instead of pulling every blob into memory.
    ///
    /// <b>Why truncate instead of converting:</b> varbinary has no implicit OR explicit
    /// conversion to vector (the vector type converts only from varchar/nvarchar/json —
    /// learn.microsoft.com/sql/t-sql/data-types/vector-data-type), so an in-place
    /// ALTER COLUMN is impossible and the old flat-float blobs would need a bespoke
    /// re-encoding pass. That pass would be dead code: embeddings are derived,
    /// instance-local, never-synced data (design.md §9.4), no production database has
    /// ever existed (the standing never-deployed caveat), and the embedding job's scan
    /// re-embeds any page whose PageEmbeddingState row is missing (EmbeddingIndexer:
    /// "a page is due when no state row records its current revision as embedded").
    /// Deleting the chunk rows AND the state rows therefore makes the system
    /// self-healing: the next indexer run rebuilds every vector through the exact
    /// pipeline production always uses. The column is dropped and re-added rather than
    /// ALTERed so this migration cannot depend on any conversion semantics at all.
    ///
    /// <b>Deliberately NOT here: CREATE VECTOR INDEX (DiskANN).</b> design.md §9.3 /
    /// data-model.md name a DiskANN cosine index on this column; verifying that DDL
    /// against SQL Server 2025's actual docs and engine (the same verification gate
    /// that kept the vector column itself out of AddPageEmbeddingState) shows it cannot
    /// be shipped in a migration today, for three engine-enforced reasons
    /// (learn.microsoft.com/sql/t-sql/statements/create-vector-index-transact-sql,
    /// checked 2026-08-23):
    ///   1. Vector indexes require ≥100 rows with non-NULL vectors AT CREATION TIME
    ///      (error 42266 below that) — a from-zero migration chain, which is every
    ///      fresh deployment and every §14 container-tier database, always runs
    ///      against an EMPTY table, so an unconditional CREATE VECTOR INDEX here would
    ///      deterministically fail every new install.
    ///   2. On boxed SQL Server 2025 the vector index is a preview feature (requires
    ///      the PREVIEW_FEATURES database-scoped configuration) and only the earlier
    ///      index format exists — which makes the indexed table READ-ONLY. A read-only
    ///      PageEmbeddings kills the §9.2 background indexer ("page saved → re-embed");
    ///      the ALLOW_STALE_VECTOR_INDEX escape hatch trades that for silently stale
    ///      search results, which this codebase does not do. The full-DML index format
    ///      is currently Azure SQL / Fabric only.
    ///   3. VECTOR_DISTANCE — the function §9.3 names for cosine similarity — is
    ///      documented to NEVER use a vector index ("Vector distance is always exact
    ///      and doesn't use any vector index, even if available"); index-assisted ANN
    ///      requires the separate, preview VECTOR_SEARCH TVF.
    /// tests/RocketWiki.SqlServer.Tests/DiskAnnIndexTripwireTests pins facts 1 and 2
    /// against the real engine every CI run: when boxed SQL Server lifts them (the
    /// full-DML index format reaching on-prem), those tests fail with instructions to
    /// revisit, and the index lands as its own reviewed migration. Until then the
    /// engine-side exact scan (VECTOR_DISTANCE over the native column) is the honest
    /// production path — the same exactness contract as before, minus shipping every
    /// blob across the wire.
    ///
    /// Like every migration in this chain, this is SQL Server DDL and only ever runs
    /// against SQL Server: the SQLite tier builds its schema from the live model via
    /// EnsureCreated() (where Embedding stays a blob — see RocketWikiDbContext) and
    /// never replays migrations. Runs inside the migration's normal transaction —
    /// unlike InitialCreate's FULLTEXT DDL there is no transaction restriction — so a
    /// partial failure rolls back the whole step loudly instead of leaving a
    /// half-converted table.
    /// </summary>
    public partial class AlterPageEmbeddingToNativeVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Derived data only (design.md §9.4). Deleting the state rows alongside the
            // chunk rows is what makes this self-healing: state-row absence is the
            // indexer's "due" signal, so every page re-embeds into the new column on
            // the next run. DELETE rather than TRUNCATE: same result at any realistic
            // size here, and vector-indexed tables reject TRUNCATE — better not to
            // build the habit on the one table that will carry that index.
            migrationBuilder.Sql("DELETE FROM PageEmbeddingStates;");
            migrationBuilder.Sql("DELETE FROM PageEmbeddings;");

            // Drop + re-add, NOT ALTER: varbinary does not convert to vector, and the
            // table was just emptied, so recreating the column is the only variant with
            // no data-conversion semantics to reason about.
            migrationBuilder.Sql("ALTER TABLE PageEmbeddings DROP COLUMN Embedding;");
            migrationBuilder.Sql("ALTER TABLE PageEmbeddings ADD Embedding vector(1536) NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric: vector does not convert to varbinary either, and the rows are
            // derived — empty the table, swap the column back, let the indexer rebuild.
            migrationBuilder.Sql("DELETE FROM PageEmbeddingStates;");
            migrationBuilder.Sql("DELETE FROM PageEmbeddings;");
            migrationBuilder.Sql("ALTER TABLE PageEmbeddings DROP COLUMN Embedding;");
            migrationBuilder.Sql("ALTER TABLE PageEmbeddings ADD Embedding varbinary(max) NOT NULL;");
        }
    }
}
