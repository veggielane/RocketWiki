using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// Closes the README "Explicitly unverified" item: "the checked-in EF Core migrations
/// are SQL Server-specific and have never actually been applied to a real SQL Server"
/// - the single highest-value claim this tier retires. Constructing the base class IS
/// the act under test: <c>Database.Migrate()</c> against a brand-new database on a
/// real (FTS-enabled) SQL Server, replaying every checked-in migration from zero.
/// The facts asserted afterwards are the ones SQLite's EnsureCreated tier structurally
/// cannot verify: the raw-SQL FULLTEXT DDL (including that it survives EF's
/// migration-transaction handling - see InitialCreate's suppressTransaction note),
/// filtered indexes, CHECK constraints, and the AuditEvents IDENTITY-inside-a-
/// composite-key mapping (SqliteAuditEventIdGenerator exists precisely because SQLite
/// cannot do that natively; the native half had never run anywhere).
/// </summary>
public sealed class MigrationTests : SqlServerTestBase
{
    public MigrationTests(SqlServerContainerFixture fixture)
        : base(fixture)
    {
    }

    [SqlServerFact]
    public async Task Migrate_FromZero_AppliesEveryCheckedInMigration_NothingPending()
    {
        using var context = CreateContext();

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = await context.Database.GetPendingMigrationsAsync();

        Assert.Contains("20260821205534_InitialCreate", applied);
        Assert.Contains("20260823084715_AddPageEmbeddingState", applied);
        Assert.Contains("20260823103019_AddGitLabCredentials", applied);
        Assert.Contains("20260823133434_AddUserAvatars", applied);
        Assert.Contains("20260823134425_AddCustomEmojis", applied);
        Assert.Equal(5, applied.Count);
        Assert.Empty(pending);
    }

    [SqlServerFact]
    public async Task FullTextIndex_ExistsOnPages_RightCatalogColumnsAndChangeTracking()
    {
        // design.md §9.1 / data-model.md: FULLTEXT on Pages(Title, CurrentContent),
        // catalog PageSearchCatalog, CHANGE_TRACKING AUTO. Asserted via the catalog
        // views because this DDL lives outside EF's model - no EF API can see it.
        var catalog = await ExecuteScalarAsync<string>("""
            SELECT c.name
            FROM sys.fulltext_indexes fi
            INNER JOIN sys.fulltext_catalogs c ON c.fulltext_catalog_id = fi.fulltext_catalog_id
            WHERE fi.object_id = OBJECT_ID('dbo.Pages')
            """);
        Assert.Equal("PageSearchCatalog", catalog);

        var changeTracking = await ExecuteScalarAsync<string>("""
            SELECT change_tracking_state_desc
            FROM sys.fulltext_indexes
            WHERE object_id = OBJECT_ID('dbo.Pages')
            """);
        Assert.Equal("AUTO", changeTracking);

        var indexedColumns = await ExecuteColumnAsync("""
            SELECT col.name
            FROM sys.fulltext_index_columns fic
            INNER JOIN sys.columns col
                ON col.object_id = fic.object_id AND col.column_id = fic.column_id
            WHERE fic.object_id = OBJECT_ID('dbo.Pages')
            ORDER BY col.name
            """);
        Assert.Equal(["CurrentContent", "Title"], indexedColumns);
    }

    [SqlServerFact]
    public async Task CheckConstraints_FilteredIndexes_AndAuditIdentity_LandAsDeclared()
    {
        // CHECK constraints (also enforced by SQLite, but the *migration SQL* that
        // creates them on SQL Server had never run).
        var checkConstraints = await ExecuteColumnAsync(
            "SELECT name FROM sys.check_constraints ORDER BY name");
        Assert.Contains("CK_AccessRules_KindColumnPairing", checkConstraints);
        Assert.Contains("CK_Watches_SpaceXorPage", checkConstraints);

        // Filtered / unique indexes data-model.md relies on. SQLite approximates
        // partial indexes; here they must exist with has_filter actually set.
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Pages_DeleteBatchId' AND has_filter = 1"));
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Pages_Space_Parent_Slug' AND has_filter = 1 AND is_unique = 1"));
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Notifications_Recipient_Unread' AND has_filter = 1"));
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_SyncOutboxEvents_SpaceId_SequenceNumber' AND is_unique = 1"));

        // AddUserAvatars: the two email-hash lookup indexes must land filtered
        // (NOT NULL) and deliberately non-unique - User.Email itself is not unique,
        // so the gravatar lookup resolves ties instead of the schema forbidding them.
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_UserAvatars_EmailHashMd5' AND has_filter = 1 AND is_unique = 0"));
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_UserAvatars_EmailHashSha256' AND has_filter = 1 AND is_unique = 0"));
        // AddCustomEmojis: the registry's uniqueness lives in this index (the grammar
        // keeps it effectively case-insensitive - lowercase only can ever be inserted),
        // so it must land unique on the real engine, not just under SQLite's EnsureCreated.
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_CustomEmojis_Name' AND is_unique = 1"));

        // data-model.md: AuditEvents clustered PK (TimestampUtc, Id) with Id remaining
        // a native bigint IDENTITY despite being only part of the key.
        Assert.Equal(1, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.identity_columns
            WHERE object_id = OBJECT_ID('dbo.AuditEvents') AND name = 'Id'
            """));

        var primaryKeyColumns = await ExecuteColumnAsync("""
            SELECT col.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns col
                ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.AuditEvents') AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal
            """);
        Assert.Equal(["TimestampUtc", "Id"], primaryKeyColumns);
    }

    private async Task<T?> ExecuteScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private async Task<List<string>> ExecuteColumnAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
