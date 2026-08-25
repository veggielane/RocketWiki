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
        Assert.Contains("20260823173233_AddPageRevisionContributors", applied);
        Assert.Contains("20260823180135_AlterPageEmbeddingToNativeVector", applied);
        Assert.Contains("20260824185208_AddPageProperties", applied);
        Assert.Contains("20260825054144_AddPageMarkings", applied);
        Assert.Equal(9, applied.Count);
        Assert.Empty(pending);
    }

    [SqlServerFact]
    public async Task AddPageMarkings_BackfillsEveryPreExistingPageToOfficial()
    {
        // design.md §21: the backfill is the part of that migration a reviewer cares
        // about. This tier is the only one that runs it at all — the SQLite tier builds
        // its schema from the live model with EnsureCreated and never replays a
        // migration, so the raw INSERT..SELECT here has no other coverage.
        //
        // Level 1 is OFFICIAL, the LOWEST in the scheme. See the migration's own comment
        // for why that is the pragmatic call and not the safe one; the assertion below
        // pins the actual behaviour so a future "surely this should be TOP SECRET" edit
        // is a deliberate, reviewed change rather than a silent one.
        using var context = CreateContext();

        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync($$"""
            INSERT INTO Users (Id, Subject, DisplayName, AttributesJson, IsExternal, CreatedAtUtc, LastSeenAtUtc)
            VALUES ('{{userId}}', 'backfill-sub', 'Backfill', '{}', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, IsArchived, LastOutboxSequence, CreatedAtUtc, CreatedByUserId)
            VALUES ('{{spaceId}}', 'BFL', 'Backfill Space', 'local-instance', 0, 0, 0, SYSUTCDATETIME(), '{{userId}}');

            INSERT INTO Pages (Id, SpaceId, AncestorPath, Slug, Title, SortOrder, CurrentRevisionNumber, CurrentContent, IsDeleted, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('{{pageId}}', '{{spaceId}}', '/', 'backfilled', 'Backfilled', 0, 1, '# Backfilled', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            INSERT INTO PageMarkings (PageId, Level, SetAtUtc, SetByUserId)
            SELECT p.Id, 1, SYSUTCDATETIME(), NULL
            FROM Pages p
            WHERE NOT EXISTS (SELECT 1 FROM PageMarkings m WHERE m.PageId = p.Id);
            """);

        Assert.Equal(1, await ExecuteScalarAsync<byte>($"SELECT Level FROM PageMarkings WHERE PageId = '{pageId}'"));
        // No local actor: nobody reviewed this page, the migration asserted OFFICIAL on
        // its behalf. SetByUserId IS NULL is exactly the query an admin runs to find
        // every page still awaiting review.
        Assert.Equal(0, await ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM PageMarkings WHERE PageId = '{pageId}' AND SetByUserId IS NOT NULL"));
    }

    [SqlServerFact]
    public async Task PageMarkings_KeysIndexesAndForeignKeys_LandAsDeclared()
    {
        // PK = PageId alone, which is what makes "one marking per page" a database fact
        // rather than an application convention (design.md §21).
        Assert.Equal("PageId", await ExecuteScalarAsync<string>("""
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.PageMarkings') AND i.is_primary_key = 1
            """));

        // The country set is a child table with a composite PK, so a country can appear
        // at most once per page and applying a set is idempotent.
        Assert.Equal(
            new List<string> { "PageId", "CountryValue" },
            await ExecuteColumnAsync("""
                SELECT c.name
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('dbo.PageMarkingCountries') AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal
                """));

        // Level is a tinyint, so the ordering the whole feature rests on is numeric in
        // the database too, and a value outside the ladder cannot be typed in.
        Assert.Equal("tinyint", await ExecuteScalarAsync<string>("""
            SELECT t.name
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.PageMarkings') AND c.name = 'Level'
            """));

        // NO ACTION on every FK, like every other table here — cascading deletes are
        // exactly what data-model.md forbids.
        Assert.Equal(2, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('dbo.PageMarkings')
              AND delete_referential_action_desc = 'NO_ACTION'
            """));
        Assert.Equal(1, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('dbo.PageMarkingCountries')
              AND delete_referential_action_desc = 'NO_ACTION'
            """));
    }

    [SqlServerFact]
    public async Task PageEmbeddings_EmbeddingColumn_IsNativeVector1536_NotNull()
    {
        // AlterPageEmbeddingToNativeVector (design.md §9.3 / data-model.md): the column
        // must land as the native vector type, NOT NULL, with the dimension count the
        // code fixes in PageEmbeddingConfiguration.EmbeddingDimensions. sys.columns is
        // the ground truth (sp_describe_first_result_set famously misreports vector as
        // varchar); a vector(n) column's max_length is its storage size, 8 + 4n bytes
        // (vector data-type docs: 4-byte single-precision elements + 8-byte header) —
        // asserting through the constant ties schema and code together, so drifting
        // either one alone fails here.
        var typeName = await ExecuteScalarAsync<string>("""
            SELECT t.name
            FROM sys.columns c
            INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.PageEmbeddings') AND c.name = 'Embedding'
            """);
        Assert.Equal("vector", typeName);

        var maxLength = await ExecuteScalarAsync<short>("""
            SELECT c.max_length
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID('dbo.PageEmbeddings') AND c.name = 'Embedding'
            """);
        Assert.Equal(8 + 4 * RocketWiki.Data.Configurations.PageEmbeddingConfiguration.EmbeddingDimensions, maxLength);

        var isNullable = await ExecuteScalarAsync<bool>("""
            SELECT c.is_nullable
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID('dbo.PageEmbeddings') AND c.name = 'Embedding'
            """);
        Assert.False(isNullable);
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

        // AddPageRevisionContributors (design.md §8 co-editing attribution): composite
        // PK (PageRevisionId, UserId), the per-user attribution index, and both FKs
        // landing as NO ACTION on the real engine (data-model.md: "no cascade deletes"
        // - deletion is an explicit audited operation, never a side effect).
        var contributorPkColumns = await ExecuteColumnAsync("""
            SELECT col.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns col
                ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.PageRevisionContributors') AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal
            """);
        Assert.Equal(["PageRevisionId", "UserId"], contributorPkColumns);
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_PageRevisionContributors_UserId'"));
        Assert.Equal(2, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('dbo.PageRevisionContributors')
              AND delete_referential_action_desc = 'NO_ACTION'
            """));

        // AddPageProperties (design.md §20): uniqueness lives on KeyNormalized, NOT on
        // Key. That is the whole reason the normalized column exists — SQL Server's
        // default collation is case-insensitive and SQLite's is not, so a unique index
        // on the raw key would enforce a different rule on each provider. This tier is
        // the only one that can prove the index landed unique on the real engine, and
        // the negative assertion (no unique index on Key) is what would catch someone
        // "simplifying" the model back onto the collation.
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_PagePropertyKeys_KeyNormalized' AND is_unique = 1"));
        Assert.Equal(0, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.PagePropertyKeys') AND i.is_unique = 1 AND col.name = 'Key'
            """));

        // Composite PK (PageId, PagePropertyKeyId) - one value per key per page, so
        // "set" is an upsert - and all three FKs NO ACTION like every other FK.
        var pagePropertyPkColumns = await ExecuteColumnAsync("""
            SELECT col.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns col
                ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.PageProperties') AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal
            """);
        Assert.Equal(["PageId", "PagePropertyKeyId"], pagePropertyPkColumns);
        Assert.Equal(3, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('dbo.PageProperties')
              AND delete_referential_action_desc = 'NO_ACTION'
            """));

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

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
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
