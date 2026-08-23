using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Data.Configurations;
using RocketWiki.Data.Tests;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// Pins — against the real engine, every CI run — the reasons the
/// AlterPageEmbeddingToNativeVector migration deliberately does NOT create the DiskANN
/// vector index that design.md §9.3 / data-model.md name (see that migration's
/// doc-comment for the full argument, doc-sourced from CREATE VECTOR INDEX's reference
/// page, checked 2026-08-23):
///
///   0. Discovered by the first real run (CI #31), an even earlier blocker the docs
///      don't lead with: the engine requires the indexed table to have a clustered
///      primary key on a SINGLE 4-BYTE INT column (error 42217). PageEmbeddings'
///      key does not qualify and reshaping it for an index the query path can't
///      use anyway (VECTOR_DISTANCE is index-blind) would be absurd.
///   1. A vector index cannot be created on a table with fewer than 100 non-NULL
///      vectors (error 42266) — so a from-zero migration chain, which always runs
///      against an empty table, can never contain CREATE VECTOR INDEX.
///   2. On boxed SQL Server 2025 the index is preview-gated (PREVIEW_FEATURES) and
///      uses the earlier index format, which makes the indexed table READ-ONLY —
///      incompatible with the §9.2 background indexer ("page saved → re-embed").
///      (Probed on a synthetic table shaped to satisfy blockers 0 and 1, since
///      PageEmbeddings itself can never get past 42217.)
///
/// These are tripwires by design: when a future SQL Server build lifts either
/// limitation, the corresponding test FAILS with instructions, and shipping the
/// DiskANN index (as its own reviewed migration or an at-scale operational step)
/// must be revisited. A failure here is information, not breakage.
/// </summary>
public sealed class DiskAnnIndexTripwireTests : SqlServerTestBase
{
    private const string IndexName = "IX_PageEmbeddings_Embedding_DiskAnn";

    private const string CreateVectorIndexSql =
        $"CREATE VECTOR INDEX {IndexName} ON dbo.PageEmbeddings (Embedding) " +
        "WITH (METRIC = 'cosine', TYPE = 'diskann');";

    public DiskAnnIndexTripwireTests(SqlServerContainerFixture fixture)
        : base(fixture)
    {
    }

    [SqlServerFact]
    public async Task CreateVectorIndex_OnFewerThan100Rows_IsRejectedByTheEngine()
    {
        // This is every fresh deployment's reality at migration time (the migration
        // just emptied the table); prove the engine refuses, i.e. that CREATE VECTOR
        // INDEX in the migration chain would deterministically break every new install.
        using var context = CreateContext();
        await EnablePreviewFeaturesAsync(context);
        await SeedEmbeddingRowsAsync(context, rowCount: 5);

        var ex = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteDdlAsync(CreateVectorIndexSql));

        // The engine refuses on the FIRST unmet precondition. On PageEmbeddings'
        // actual schema that is 42217 (clustered PK must be a single 4-byte INT —
        // discovered by CI run #31, and an even stronger reason the index cannot
        // ship on this table); 42266 (>=100 non-NULL vectors) is the documented
        // row-count blocker that fires once the key shape qualifies. Either proves
        // the DDL cannot live in the migration chain; anything ELSE — above all
        // success — means the engine changed and the decision needs revisiting.
        Assert.True(ex.Number is 42217 or 42266,
            $"CREATE VECTOR INDEX on PageEmbeddings failed with unexpected error {ex.Number}: " +
            $"\"{ex.Message}\". Expected 42217 (single 4-byte INT clustered PK required) or 42266 " +
            "(>=100 non-NULL vectors required). The engine's preconditions have changed — revisit " +
            "AlterPageEmbeddingToNativeVector's decision to keep the DiskANN index out of the " +
            "migration chain.");

        Assert.Equal(0, await CountVectorIndexesAsync());
    }

    [SqlServerFact]
    public async Task CreateVectorIndex_OnAQualifyingTable_Succeeds_ButMakesItReadOnly()
    {
        // The read-only blocker, probed on a synthetic table shaped to satisfy the
        // engine's preconditions (single 4-byte INT clustered PK — which
        // PageEmbeddings itself can never satisfy, see the other tripwire — plus
        // >=100 non-NULL vectors and PREVIEW_FEATURES). Even then, the boxed-2025
        // earlier-format index freezes the table — which would kill the §9.2
        // embedding indexer. The day the INSERT below starts succeeding, boxed
        // SQL Server has the full-DML index format and the calculus changes.
        using var context = CreateContext();
        await EnablePreviewFeaturesAsync(context);

        await ExecuteDdlAsync("""
            CREATE TABLE dbo.VectorTripwireProbe (
                Id INT NOT NULL IDENTITY PRIMARY KEY CLUSTERED,
                Embedding VECTOR(8) NOT NULL);
            """);
        var seed = string.Join(";", Enumerable.Range(0, 120).Select(i =>
            $"INSERT INTO dbo.VectorTripwireProbe (Embedding) VALUES (CAST('[{i % 7 + 1}, {i % 5 + 1}, 1, 0, 0, 0, 0, 0]' AS VECTOR(8)))"));
        await ExecuteDdlAsync(seed);

        try
        {
            await ExecuteDdlAsync(
                "CREATE VECTOR INDEX IX_VectorTripwireProbe_Embedding ON dbo.VectorTripwireProbe (Embedding) " +
                "WITH (METRIC = 'cosine', TYPE = 'diskann');");
        }
        catch (SqlException ex)
        {
            Assert.Fail(
                $"CREATE VECTOR INDEX failed on a table satisfying every documented precondition " +
                $"(single INT clustered PK, 120 rows, PREVIEW_FEATURES ON) — error {ex.Number}: " +
                $"\"{ex.Message}\". The mssql/server:2025 image's vector-index preview may have " +
                "changed shape; update AlterPageEmbeddingToNativeVector's doc-comment with this finding.");
        }

        // The documented catalog view for vector indexes.
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new SqlCommand(
                "SELECT COUNT(*) FROM sys.vector_indexes WHERE object_id = OBJECT_ID('dbo.VectorTripwireProbe')",
                connection);
            Assert.Equal(1, (int)(await count.ExecuteScalarAsync())!);
        }

        // DML against the now-indexed table: expected to be REJECTED on boxed 2025.
        SqlException? insertError = null;
        try
        {
            await ExecuteDdlAsync(
                "INSERT INTO dbo.VectorTripwireProbe (Embedding) VALUES (CAST('[9, 9, 9, 0, 0, 0, 0, 0]' AS VECTOR(8)))");
        }
        catch (SqlException ex)
        {
            insertError = ex;
        }

        Assert.True(insertError is not null,
            "INSERT into a vector-indexed table SUCCEEDED — boxed SQL Server now supports full DML " +
            "on vector-indexed tables (the latest index format has reached on-prem). One obstacle to " +
            "the §9.3 DiskANN index is gone: revisit AlterPageEmbeddingToNativeVector (mind the " +
            "remaining blockers: the single-INT-PK table-shape requirement that PageEmbeddings fails, " +
            "the >=100-row creation minimum, and VECTOR_DISTANCE being index-blind).");
    }

    private async Task ExecuteDdlAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection)
        {
            // DiskANN index builds are non-trivial even at test scale; never let the
            // default 30s timeout masquerade as an engine rejection.
            CommandTimeout = 300,
        };
        await command.ExecuteNonQueryAsync();
    }

    private async Task EnablePreviewFeaturesAsync(Microsoft.EntityFrameworkCore.DbContext context)
    {
        // SQL Server 2025 gates vector indexes behind the PREVIEW_FEATURES
        // database-scoped configuration (CREATE VECTOR INDEX docs). Scoped to this
        // test's own database only.
        await context.Database.ExecuteSqlRawAsync(
            "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;");
    }

    /// <summary>Seeds one page with <paramref name="rowCount"/> chunk rows of distinct
    /// non-zero 1536-dim vectors through the real EF write path (SqlVector conversion).</summary>
    private async Task SeedEmbeddingRowsAsync(RocketWiki.Data.RocketWikiDbContext context, int rowCount)
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        context.Spaces.Add(space);
        context.Pages.Add(page);

        for (var i = 0; i < rowCount; i++)
        {
            var vector = new float[PageEmbeddingConfiguration.EmbeddingDimensions];
            for (var d = 0; d < 8; d++)
            {
                vector[d] = (float)Math.Sin((i + 1) * 0.7 + d); // distinct, non-zero, non-duplicate
            }

            context.PageEmbeddings.Add(new PageEmbedding
            {
                PageId = page.Id,
                ChunkIndex = i,
                HeadingPath = $"Section {i}",
                ChunkHash = new byte[32],
                Embedding = vector,
                Model = "fake-model",
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task<int> CountVectorIndexesAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.vector_indexes WHERE object_id = OBJECT_ID('dbo.PageEmbeddings')",
            connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

}
