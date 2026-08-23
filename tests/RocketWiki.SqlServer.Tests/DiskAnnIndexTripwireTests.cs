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
///   1. A vector index cannot be created on a table with fewer than 100 non-NULL
///      vectors (error 42266) — so a from-zero migration chain, which always runs
///      against an empty PageEmbeddings, can never contain CREATE VECTOR INDEX.
///   2. On boxed SQL Server 2025 the index is preview-gated (PREVIEW_FEATURES) and
///      uses the earlier index format, which makes the indexed table READ-ONLY —
///      incompatible with the §9.2 background indexer ("page saved → re-embed").
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

        // 42266 is the documented "at least 100 rows required" error. Any other error
        // still proves the DDL cannot ship, but should be looked at - surface it.
        Assert.True(ex.Number == 42266,
            $"CREATE VECTOR INDEX on a near-empty table failed with error {ex.Number} instead of the " +
            $"documented 42266 (>=100 non-NULL vectors required): \"{ex.Message}\". If the minimum-row " +
            "requirement was lifted, revisit AlterPageEmbeddingToNativeVector's decision to keep the " +
            "DiskANN index out of the migration chain.");

        Assert.Equal(0, await CountVectorIndexesAsync());
    }

    [SqlServerFact]
    public async Task CreateVectorIndex_With100Rows_Succeeds_ButMakesTheTableReadOnly()
    {
        // The second blocker: even created out-of-band (enough rows, preview flag on),
        // the boxed-2025 earlier-format index freezes the table - which would kill the
        // §9.2 embedding indexer. The day the INSERT below starts succeeding, boxed
        // SQL Server has the full-DML index format and the index becomes shippable.
        using var context = CreateContext();
        await EnablePreviewFeaturesAsync(context);
        await SeedEmbeddingRowsAsync(context, rowCount: 120);

        try
        {
            await ExecuteDdlAsync(CreateVectorIndexSql);
        }
        catch (SqlException ex)
        {
            Assert.Fail(
                $"CREATE VECTOR INDEX failed on the 2025 container even with {120} rows and " +
                $"PREVIEW_FEATURES ON — error {ex.Number}: \"{ex.Message}\". The mssql/server:2025 " +
                "image may lack the vector-index preview feature entirely; update " +
                "AlterPageEmbeddingToNativeVector's doc-comment with this finding.");
        }

        // The documented catalog view for vector indexes.
        Assert.Equal(1, await CountVectorIndexesAsync());

        // DML against the now-indexed table: expected to be REJECTED on boxed 2025.
        var insertError = await TryInsertOneMoreRowAsync(context);
        Assert.True(insertError is not null,
            "INSERT into a vector-indexed PageEmbeddings SUCCEEDED — boxed SQL Server now supports " +
            "full DML on vector-indexed tables (the latest index format has reached on-prem). The main " +
            "obstacle to the §9.3 DiskANN index is gone: revisit AlterPageEmbeddingToNativeVector and " +
            "plan the index (mind the >=100-row creation minimum, which still rules out the from-zero " +
            "migration chain).");
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

    private async Task<SqlException?> TryInsertOneMoreRowAsync(RocketWiki.Data.RocketWikiDbContext context)
    {
        var pageId = await context.Pages.Select(p => p.Id).FirstAsync();
        context.PageEmbeddings.Add(new PageEmbedding
        {
            PageId = pageId,
            ChunkIndex = 100_000,
            HeadingPath = "Post-index insert",
            ChunkHash = new byte[32],
            Embedding = Enumerable.Range(0, PageEmbeddingConfiguration.EmbeddingDimensions)
                .Select(i => i == 0 ? 1f : 0f).ToArray(),
            Model = "fake-model",
            UpdatedAtUtc = DateTime.UtcNow,
        });

        try
        {
            await context.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException sqlEx)
        {
            return sqlEx;
        }
    }
}
