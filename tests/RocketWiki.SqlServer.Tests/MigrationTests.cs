using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RocketWiki.Data;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
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
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-migration", "127.0.0.1");

    private readonly SqlServerContainerFixture _fixture;

    public MigrationTests(SqlServerContainerFixture fixture)
        : base(fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// <b>The backfill actually copies.</b> Every other test in this class starts from a
    /// fully-migrated empty database, where <c>AddSpaceOwner</c>'s UPDATE runs over zero
    /// rows — that proves the SQL parses against a real engine but says nothing about
    /// whether existing spaces come out owned. So this one migrates to the migration
    /// BEFORE it, writes a space the old way, and then migrates up.
    ///
    /// <para>The insert is raw SQL on purpose: at that point the <c>OwnerUserId</c> column
    /// does not exist yet, and the live EF model would try to write it.</para>
    ///
    /// <para>Both rows matter. The native space proves the creator becomes the owner; the
    /// replica — <c>CreatedByUserId = Guid.Empty</c>, exactly what
    /// <c>BundleImportService</c> writes because users never cross the sync boundary
    /// (§12) — proves the migration does not choke on the rows a high-side instance holds
    /// most of, and lands them honestly ownerless rather than failing or inventing an owner.</para>
    /// </summary>
    [SqlServerFact]
    public async Task AddSpaceOwner_BackfillsOwnerFromCreator_OnSpacesThatAlreadyExisted()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        var options = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlServer(connectionString)
            .UseLocalInstanceId("local-instance")
            .Options;

        var creatorId = Guid.NewGuid();
        var nativeSpaceId = Guid.NewGuid();
        var replicaSpaceId = Guid.NewGuid();

        using (var context = new RocketWikiDbContext(options))
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate("20260830073044_AddEmbeddingFailedRevisionNumber");

            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, LastOutboxSequence, IsDeleted, CreatedAtUtc, CreatedByUserId)
                VALUES
                    ('{nativeSpaceId}', 'NATIVE', 'A native space', 'local-instance', 0, 0, 0, SYSUTCDATETIME(), '{creatorId}'),
                    ('{replicaSpaceId}', 'REPLICA', 'An imported space', 'some-low-instance', 0, 0, 0, SYSUTCDATETIME(), '{Guid.Empty}');
                """);

            migrator.Migrate();
        }

        using (var context = new RocketWikiDbContext(options))
        {
            var native = await context.Spaces.SingleAsync(s => s.Id == nativeSpaceId);
            Assert.Equal(creatorId, native.OwnerUserId);

            // Honestly ownerless rather than fabricated — Space.owner resolves to null,
            // and a high-side admin assigns a real one through setSpaceOwner.
            var replica = await context.Spaces.SingleAsync(s => s.Id == replicaSpaceId);
            Assert.Equal(Guid.Empty, replica.OwnerUserId);
        }
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
        Assert.Contains("20260825062334_AddPageMarkingPrefix", applied);
        Assert.Contains("20260829090727_AddPageIcon", applied);
        Assert.Contains("20260829092438_SpaceUniquePageSlug", applied);
        Assert.Contains("20260829134601_AddPageEntries", applied);
        Assert.Contains("20260829192825_BinaryCollationOnStringKeys", applied);
        Assert.Contains("20260830055858_CanonicalizeSpaceKeysAndPageSlugs", applied);
        Assert.Contains("20260830073044_AddEmbeddingFailedRevisionNumber", applied);
        Assert.Contains("20260831072347_AddSpaceOwner", applied);
        Assert.Contains("20260902222520_AddMarkingSelectorsAndFixedCaveat", applied);
        Assert.Contains("20260902224034_SplitSpaceGrantsIntoAccessAndRole", applied);
        Assert.Contains("20260904230211_AddMarkingUnavailableFlag", applied);
        Assert.Equal(20, applied.Count);
        Assert.Empty(pending);
    }

    /// <summary>
    /// <b>The grant split is behaviour-preserving on existing rows</b> (design.md §6.4):
    /// migrated to the migration BEFORE it, three pre-split grants written the old way — a
    /// Viewer, an Editor and a SpaceAdmin — then migrated up. The Viewer row must become an
    /// access grant IN PLACE (same id); each of the other two must gain a MIRROR access
    /// grant with the same expression and audit columns; and the new check constraint must
    /// refuse the retired Viewer role and accept the new kind. Raw SQL for the seed because
    /// the live model's constraint would refuse a Viewer row.
    /// </summary>
    [SqlServerFact]
    public async Task SplitSpaceGrants_ConvertsViewerRowsToAccess_MirrorsEditorAndAdminGrants_AndLandsTheNewCheckConstraint()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        var options = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlServer(connectionString)
            .UseLocalInstanceId("local-instance")
            .Options;

        // Every query in this test targets the fresh database above, not the base class's.
        Task NonQuery(string sql) => ExecuteNonQueryAsync(sql, connectionString);
        Task<T?> Scalar<T>(string sql) => ExecuteScalarAsync<T>(sql, connectionString);
        Task<List<string>> Column(string sql) => ExecuteColumnAsync(sql, connectionString);

        var userId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var viewerRule = Guid.CreateVersion7();
        var editorRule = Guid.CreateVersion7();
        var adminRule = Guid.CreateVersion7();

        using (var context = new RocketWikiDbContext(options))
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate("20260902222520_AddMarkingSelectorsAndFixedCaveat");

            await NonQuery($$"""
                INSERT INTO Users (Id, Subject, DisplayName, AttributesJson, IsExternal, CreatedAtUtc, LastSeenAtUtc)
                VALUES ('{{userId}}', 'split-sub', 'Split', '{}', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

                INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, IsDeleted, LastOutboxSequence, CreatedAtUtc, CreatedByUserId, OwnerUserId)
                VALUES ('{{spaceId}}', 'SPL', 'Split Space', 'local-instance', 0, 0, 0, SYSUTCDATETIME(), '{{userId}}', '{{userId}}');

                INSERT INTO AccessRules (Id, Kind, SpaceId, PageId, Role, Action, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
                VALUES
                    ('{{viewerRule}}', 1, '{{spaceId}}', NULL, 1, NULL, '{ "everyone": true }', '2026-01-01T00:00:00', '{{userId}}', '2026-01-01T00:00:00', '{{userId}}'),
                    ('{{editorRule}}', 1, '{{spaceId}}', NULL, 2, NULL, '{ "group": "engineering" }', '2026-01-02T00:00:00', '{{userId}}', '2026-01-02T00:00:00', '{{userId}}'),
                    ('{{adminRule}}', 1, '{{spaceId}}', NULL, 3, NULL, '{ "group": "space-admins" }', '2026-01-03T00:00:00', '{{userId}}', '2026-01-03T00:00:00', '{{userId}}');
                """);

            migrator.Migrate();
        }

        // The Viewer row: same id, now an access grant, no role.
        Assert.Equal(3, await Scalar<byte>($"SELECT Kind FROM AccessRules WHERE Id = '{viewerRule}'"));
        Assert.Null(await Scalar<byte?>($"SELECT Role FROM AccessRules WHERE Id = '{viewerRule}'"));

        // The Editor and SpaceAdmin rows: untouched, and each mirrored by an access grant
        // with the same expression and audit columns (the review query's key).
        Assert.Equal(2, await Scalar<byte>($"SELECT Role FROM AccessRules WHERE Id = '{editorRule}'"));
        Assert.Equal(3, await Scalar<byte>($"SELECT Role FROM AccessRules WHERE Id = '{adminRule}'"));
        foreach (var (expression, createdAt) in new[]
        {
            ("{ \"group\": \"engineering\" }", "2026-01-02T00:00:00"),
            ("{ \"group\": \"space-admins\" }", "2026-01-03T00:00:00"),
        })
        {
            Assert.Equal(1, await Scalar<int>($"""
                SELECT COUNT(*) FROM AccessRules
                WHERE Kind = 3 AND SpaceId = '{spaceId}' AND Role IS NULL AND PageId IS NULL AND Action IS NULL
                  AND ExpressionJson = '{expression}' AND CreatedAtUtc = '{createdAt}' AND CreatedByUserId = '{userId}'
                """));
        }

        // Five rows in all: three originals (one converted) plus two mirrors — nothing lost,
        // nothing invented beyond the mirrors.
        Assert.Equal(5, await Scalar<int>($"SELECT COUNT(*) FROM AccessRules WHERE SpaceId = '{spaceId}'"));
        Assert.Equal(3, await Scalar<int>($"SELECT COUNT(*) FROM AccessRules WHERE SpaceId = '{spaceId}' AND Kind = 3"));

        // The NEW constraint landed: a Viewer-valued role grant is refused, a role grant
        // with NO role is refused (a CHECK passes on NULL unless the clause says
        // otherwise - the defect the SQLite tier caught in the design's original text),
        // an access grant with a role is refused, a plain access grant is accepted.
        await Assert.ThrowsAsync<SqlException>(() => NonQuery($$"""
            INSERT INTO AccessRules (Id, Kind, SpaceId, PageId, Role, Action, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
            VALUES (NEWID(), 1, '{{spaceId}}', NULL, 1, NULL, '{ "everyone": true }', SYSUTCDATETIME(), '{{userId}}', SYSUTCDATETIME(), '{{userId}}');
            """));
        await Assert.ThrowsAsync<SqlException>(() => NonQuery($$"""
            INSERT INTO AccessRules (Id, Kind, SpaceId, PageId, Role, Action, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
            VALUES (NEWID(), 1, '{{spaceId}}', NULL, NULL, NULL, '{ "everyone": true }', SYSUTCDATETIME(), '{{userId}}', SYSUTCDATETIME(), '{{userId}}');
            """));
        await Assert.ThrowsAsync<SqlException>(() => NonQuery($$"""
            INSERT INTO AccessRules (Id, Kind, SpaceId, PageId, Role, Action, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
            VALUES (NEWID(), 3, '{{spaceId}}', NULL, 2, NULL, '{ "everyone": true }', SYSUTCDATETIME(), '{{userId}}', SYSUTCDATETIME(), '{{userId}}');
            """));
        await NonQuery($$"""
            INSERT INTO AccessRules (Id, Kind, SpaceId, PageId, Role, Action, ExpressionJson, CreatedAtUtc, CreatedByUserId, UpdatedAtUtc, UpdatedByUserId)
            VALUES (NEWID(), 3, '{{spaceId}}', NULL, NULL, NULL, '{ "group": "late" }', SYSUTCDATETIME(), '{{userId}}', SYSUTCDATETIME(), '{{userId}}');
            """);

        // And the selector child table is there with its PK and NO ACTION FK.
        Assert.Equal(
            new List<string> { "AccessRuleId", "Category", "Value" },
            await Column("""
                SELECT c.name
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('dbo.AccessRuleSelectors') AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal
                """));
        Assert.Equal(1, await Scalar<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('dbo.AccessRuleSelectors')
              AND delete_referential_action_desc = 'NO_ACTION'
            """));
    }

    /// <summary>
    /// <b>The data steps of <c>AddMarkingSelectorsAndFixedCaveat</c> actually run over
    /// existing rows</b> (design.md §21.4/§21.12): migrated to the migration BEFORE it,
    /// rows written the old way — a free-text prefix on a page and on an entry, a legacy
    /// <c>GB</c> caveat alone, a <c>GB</c> beside a <c>UK</c> (the de-duplication case
    /// the primary key forces), and a token outside the fixed set that must be LEFT
    /// ALONE — then migrated up. Every other test in this class starts from a
    /// fully-migrated empty database, where these UPDATEs run over zero rows.
    ///
    /// <para>Raw SQL rather than the live model, because at that point the
    /// <c>PageMarkingSelectors</c> table does not exist and the entity would try to write
    /// its navigation.</para>
    /// </summary>
    [SqlServerFact]
    public async Task AddMarkingSelectors_NullsNonUkPrefixes_AndRemapsGbToUk()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        var options = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlServer(connectionString)
            .UseLocalInstanceId("local-instance")
            .Options;

        // Every query in this test targets the fresh database above, not the base class's.
        Task NonQuery(string sql) => ExecuteNonQueryAsync(sql, connectionString);
        Task<T?> Scalar<T>(string sql) => ExecuteScalarAsync<T>(sql, connectionString);
        Task<List<string>> Column(string sql) => ExecuteColumnAsync(sql, connectionString);

        var userId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var natoPage = Guid.CreateVersion7();     // NATO prefix, GB caveat -> UK prefix cleared, UK caveat
        var ukPage = Guid.CreateVersion7();       // UK prefix, GB + UK caveat -> one UK row
        var bareFrPage = Guid.CreateVersion7();   // no prefix, FR caveat -> untouched (fails closed, review query)
        var entryId = Guid.CreateVersion7();      // entry with a free-text prefix and a GB caveat

        using (var context = new RocketWikiDbContext(options))
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate("20260831072347_AddSpaceOwner");

            await NonQuery($$"""
                INSERT INTO Users (Id, Subject, DisplayName, AttributesJson, IsExternal, CreatedAtUtc, LastSeenAtUtc)
                VALUES ('{{userId}}', 'caveat-sub', 'Caveat', '{}', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

                INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, IsDeleted, LastOutboxSequence, CreatedAtUtc, CreatedByUserId, OwnerUserId)
                VALUES ('{{spaceId}}', 'CAV', 'Caveat Space', 'local-instance', 0, 0, 0, SYSUTCDATETIME(), '{{userId}}', '{{userId}}');

                INSERT INTO Pages (Id, SpaceId, AncestorPath, Slug, Title, SortOrder, CurrentRevisionNumber, CurrentContent, IsDeleted, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    ('{{natoPage}}', '{{spaceId}}', '/', 'nato', 'Nato', 0, 1, '# Nato', 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    ('{{ukPage}}', '{{spaceId}}', '/', 'uk', 'Uk', 1, 1, '# Uk', 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    ('{{bareFrPage}}', '{{spaceId}}', '/', 'fr', 'Fr', 2, 1, '# Fr', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

                INSERT INTO PageMarkings (PageId, Level, Prefix, SetAtUtc, SetByUserId)
                VALUES
                    ('{{natoPage}}', 3, 'NATO', SYSUTCDATETIME(), NULL),
                    ('{{ukPage}}', 3, 'UK', SYSUTCDATETIME(), NULL),
                    ('{{bareFrPage}}', 3, NULL, SYSUTCDATETIME(), NULL);

                INSERT INTO PageMarkingCountries (PageId, CountryValue)
                VALUES
                    ('{{natoPage}}', 'GB'),
                    ('{{ukPage}}', 'GB'),
                    ('{{ukPage}}', 'UK'),
                    ('{{bareFrPage}}', 'FR');

                INSERT INTO PageEntries (Id, PageId, Collection, Data, Version, Level, Prefix, CreatedAtUtc, UpdatedAtUtc, UpdatedByUserId, IsDeleted)
                VALUES ('{{entryId}}', '{{natoPage}}', 'incident', '{}', 1, 1, 'UK/US', SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, 0);

                INSERT INTO PageEntryCountries (PageEntryId, CountryValue)
                VALUES ('{{entryId}}', 'GB');
                """);

            migrator.Migrate();
        }

        // Prefixes: the toggle has two states, so everything but UK is cleared.
        Assert.Null(await Scalar<string>($"SELECT Prefix FROM PageMarkings WHERE PageId = '{natoPage}'"));
        Assert.Equal("UK", await Scalar<string>($"SELECT Prefix FROM PageMarkings WHERE PageId = '{ukPage}'"));
        Assert.Null(await Scalar<string>($"SELECT Prefix FROM PageEntries WHERE Id = '{entryId}'"));

        // GB -> UK, and the (GB, UK) pair collapses to one UK row rather than violating
        // the primary key.
        Assert.Equal(["UK"], await Column($"SELECT CountryValue FROM PageMarkingCountries WHERE PageId = '{natoPage}'"));
        Assert.Equal(["UK"], await Column($"SELECT CountryValue FROM PageMarkingCountries WHERE PageId = '{ukPage}'"));
        Assert.Equal(["UK"], await Column($"SELECT CountryValue FROM PageEntryCountries WHERE PageEntryId = '{entryId}'"));

        // A token outside the fixed set is left in place — it fails closed rather than
        // being guessed at — and the migration's review query finds it.
        Assert.Equal(["FR"], await Column($"SELECT CountryValue FROM PageMarkingCountries WHERE PageId = '{bareFrPage}'"));
        Assert.Equal(["FR"], await Column(
            "SELECT CountryValue FROM PageMarkingCountries WHERE CountryValue NOT IN ('AUS','CAN','NZ','UK','US')"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM PageMarkingCountries WHERE CountryValue = 'GB'"));
    }

    [SqlServerFact]
    public async Task PageMarkingSelectors_KeysIndexesAndForeignKeys_LandAsDeclared()
    {
        // PK (PageId, Category) — the category, NOT the value — is what makes "one value
        // per category on a page" a database fact (design.md §21.15).
        Assert.Equal(
            new List<string> { "PageId", "Category" },
            await ExecuteColumnAsync("""
                SELECT c.name
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('dbo.PageMarkingSelectors') AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal
                """));

        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_PageMarkingSelectors_Category_Value_PageId'"));

        // nvarchar(32) on both tokens: 32 UTF-16 code units is 64 bytes of max_length.
        foreach (var column in new[] { "Category", "Value" })
        {
            Assert.Equal(64, await ExecuteScalarAsync<short>($"""
                SELECT c.max_length
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID('dbo.PageMarkingSelectors') AND c.name = '{column}'
                """));
        }

        // NO ACTION, like every other FK here.
        Assert.Equal(1, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('dbo.PageMarkingSelectors')
              AND delete_referential_action_desc = 'NO_ACTION'
            """));
    }

    /// <summary>
    /// "EF Core migrations are checked in and reviewed like code" (design.md §14) was,
    /// until this test, protected only by whoever remembered to run
    /// <c>dotnet ef migrations add</c>. Neither tier could see a forgotten one:
    ///
    /// <list type="bullet">
    /// <item>The SQLite tier builds its schema from the LIVE C# model with
    /// <c>EnsureCreated</c> (SqliteTestBase's own doc says so), so a model change with no
    /// migration behind it is exactly what it tests against — green, always.</item>
    /// <item>The sibling test above asserts <c>GetPendingMigrationsAsync()</c> is empty,
    /// which is about migration FILES that have not been applied — the opposite
    /// direction. A model change with no scaffolded migration leaves that set empty too.</item>
    /// </list>
    ///
    /// <para><c>HasPendingModelChanges</c> is the one check that compares the live model
    /// against the last migration's snapshot, so an added index, a changed
    /// <c>HasMaxLength</c>, a new filter or a new collation cannot ship green here and
    /// wrong in production. It belongs in this tier and only this tier: the comparison is
    /// provider-specific (the snapshot is scaffolded on SQL Server via
    /// RocketWikiDbContextFactory, and RocketWikiDbContext deliberately branches its model
    /// by provider for the vector column and the PageEntry collation), so asking it on
    /// SQLite would report drift that does not exist.</para>
    ///
    /// <para>If this fails: run <c>dotnet ef migrations add &lt;Name&gt; --project
    /// src/RocketWiki.Data</c>, review the generated SQL like any other code, and update
    /// the migration list in the test above.</para>
    /// </summary>
    [SqlServerFact]
    public void Model_HasNoChangesTheCheckedInMigrationsDoNotDescribe()
    {
        using var context = CreateContext();

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF model has changes no checked-in migration describes (design.md §14). " +
            "Scaffold one with `dotnet ef migrations add <Name> --project src/RocketWiki.Data`, " +
            "review its SQL, and add it to Migrate_FromZero_AppliesEveryCheckedInMigration_NothingPending's list.");
    }

    [SqlServerFact]
    public async Task AddPageMarkingPrefix_SetsUkOnEveryPreExistingMarking_AndLeavesTheColumnNullable()
    {
        // design.md §21.12. A SECOND migration on top of AddPageMarkings rather than an
        // edit to it: that one is already applied here, and editing an applied migration
        // makes the from-zero replay above a test of a fiction.
        //
        // Unlike the OFFICIAL backfill this carries no security risk and needs no review
        // sweep — the prefix grants and denies nothing, so asserting UK on a page nobody
        // has looked at cannot change who can read it.
        using var context = CreateContext();

        var pageId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync($$"""
            INSERT INTO Users (Id, Subject, DisplayName, AttributesJson, IsExternal, CreatedAtUtc, LastSeenAtUtc)
            VALUES ('{{userId}}', 'prefix-sub', 'Prefix', '{}', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, IsDeleted, LastOutboxSequence, CreatedAtUtc, CreatedByUserId)
            VALUES ('{{spaceId}}', 'PFX', 'Prefix Space', 'local-instance', 0, 0, 0, SYSUTCDATETIME(), '{{userId}}');

            INSERT INTO Pages (Id, SpaceId, AncestorPath, Slug, Title, SortOrder, CurrentRevisionNumber, CurrentContent, IsDeleted, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('{{pageId}}', '{{spaceId}}', '/', 'prefixed', 'Prefixed', 0, 1, '# Prefixed', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            -- A row as AddPageMarkings' backfill would have left it: no prefix yet.
            INSERT INTO PageMarkings (PageId, Level, Prefix, SetAtUtc, SetByUserId)
            VALUES ('{{pageId}}', 1, NULL, SYSUTCDATETIME(), NULL);

            UPDATE PageMarkings SET Prefix = 'UK' WHERE Prefix IS NULL;
            """);

        Assert.Equal("UK", await ExecuteScalarAsync<string>($"SELECT Prefix FROM PageMarkings WHERE PageId = '{pageId}'"));

        // NULLABLE, because "no prefix" is a legal marking and an editor must be able to
        // clear it — a NOT NULL column would have forced a sentinel.
        Assert.Equal(1, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.PageMarkings') AND name = 'Prefix' AND is_nullable = 1
            """));

        // nvarchar(16): 16 UTF-16 code units is 32 bytes of max_length.
        Assert.Equal(32, await ExecuteScalarAsync<short>("""
            SELECT c.max_length
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID('dbo.PageMarkings') AND c.name = 'Prefix'
            """));

        // Deliberately UNINDEXED: the prefix gates nothing, so nothing looks a page up by
        // it. Only the PK and the Level index exist on this table.
        Assert.Equal(0, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID('dbo.PageMarkings') AND c.name = 'Prefix'
            """));
    }

    [SqlServerFact]
    public async Task AddMarkingUnavailableFlag_LeavesEveryPreExistingRowAvailable_AndOnlyTheFlagDeniesEveryone()
    {
        // design.md §21.10. The column exists so "we do not know this page's marking" is a
        // state the database can hold. Before it, the importer persisted that state as a
        // bare prefix-less TOP SECRET, and the level-blind ladder (§21.12) reads such a
        // row as readable by everyone the space admits. The migration cannot tell that
        // row from a genuine TOP SECRET an editor set with the prefix toggled off, so it
        // defaults EVERY existing row to available and repairs nothing — this test pins
        // that honestly rather than letting anyone believe the migration closes the gap
        // for old data. The migration's own comment carries the review query an operator
        // runs to find candidates; the second half here proves the repair it prescribes
        // is the only thing that makes such a row unreadable, and that the flag — not the
        // level — is what ToMarking() consults.
        using var context = CreateContext();

        var pageId = Guid.CreateVersion7();
        var entryId = Guid.CreateVersion7();
        var spaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await ExecuteNonQueryAsync($$"""
            INSERT INTO Users (Id, Subject, DisplayName, AttributesJson, IsExternal, CreatedAtUtc, LastSeenAtUtc)
            VALUES ('{{userId}}', 'unavailable-sub', 'Unavailable', '{}', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, IsDeleted, LastOutboxSequence, CreatedAtUtc, CreatedByUserId)
            VALUES ('{{spaceId}}', 'UNV', 'Unavailable Space', 'local-instance', 0, 0, 0, SYSUTCDATETIME(), '{{userId}}');

            INSERT INTO Pages (Id, SpaceId, AncestorPath, Slug, Title, SortOrder, CurrentRevisionNumber, CurrentContent, IsDeleted, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('{{pageId}}', '{{spaceId}}', '/', 'unavailable', 'Unavailable', 0, 1, '# Unavailable', 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            -- Both rows shaped exactly as the OLDER importer wrote "no marking": TOP SECRET
            -- (4), no prefix, no countries, no actor — and, being pre-migration rows, they
            -- do not name IsUnavailable at all. The column default is what fills it.
            INSERT INTO PageMarkings (PageId, Level, Prefix, SetAtUtc, SetByUserId)
            VALUES ('{{pageId}}', 4, NULL, SYSUTCDATETIME(), NULL);

            INSERT INTO PageEntries (Id, PageId, Collection, Data, Version, Level, Prefix, CreatedAtUtc, UpdatedAtUtc, UpdatedByUserId, IsDeleted)
            VALUES ('{{entryId}}', '{{pageId}}', 'incident', '{}', 1, 4, NULL, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, 0);
            """);

        // NOT NULL bit on both tables: "unknown" is a fact every row states, never a
        // NULL a reader could interpret either way.
        Assert.Equal(2, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.name = 'IsUnavailable' AND c.is_nullable = 0 AND t.name = 'bit'
              AND c.object_id IN (OBJECT_ID('dbo.PageMarkings'), OBJECT_ID('dbo.PageEntries'))
            """));

        // Deliberately UNINDEXED (see the migration comment): the only enforcement read
        // is the per-row PK lookup.
        Assert.Equal(0, await ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE c.name = 'IsUnavailable'
              AND ic.object_id IN (OBJECT_ID('dbo.PageMarkings'), OBJECT_ID('dbo.PageEntries'))
            """));

        // The honest part: a pre-existing row defaults to AVAILABLE, and reads back as an
        // ordinary TOP SECRET, not as the sentinel. No backfill happened.
        Assert.False(await ExecuteScalarAsync<bool>($"SELECT IsUnavailable FROM PageMarkings WHERE PageId = '{pageId}'"));
        Assert.False(await ExecuteScalarAsync<bool>($"SELECT IsUnavailable FROM PageEntries WHERE Id = '{entryId}'"));

        var markingBefore = (await context.Set<PageMarking>()
            .Include(m => m.Countries).Include(m => m.Selectors)
            .SingleAsync(m => m.PageId == pageId)).ToMarking();
        Assert.False(markingBefore.IsUnavailable);
        Assert.Equal(ClassificationLevel.TopSecret, markingBefore.Level);

        var entryBefore = (await context.Set<PageEntry>()
            .Include(e => e.Countries)
            .SingleAsync(e => e.Id == entryId)).ToMarking();
        Assert.False(entryBefore.IsUnavailable);
        Assert.Equal(ClassificationLevel.TopSecret, entryBefore.Level);

        // The migration's review query — restricted to this page so a shared database
        // cannot make the count lie — finds exactly these two candidates.
        Assert.Equal(1, await ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
            FROM PageMarkings m
            WHERE m.IsUnavailable = 0
              AND m.Level = 4
              AND m.Prefix IS NULL
              AND m.SetByUserId IS NULL
              AND NOT EXISTS (SELECT 1 FROM PageMarkingCountries c WHERE c.PageId = m.PageId)
              AND NOT EXISTS (SELECT 1 FROM PageMarkingSelectors s WHERE s.PageId = m.PageId)
              AND m.PageId = '{pageId}'
            """));
        Assert.Equal(1, await ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
            FROM PageEntries e
            WHERE e.IsUnavailable = 0
              AND e.Level = 4 AND e.Prefix IS NULL AND e.UpdatedByUserId IS NULL
              AND NOT EXISTS (SELECT 1 FROM PageEntryCountries c WHERE c.PageEntryId = e.Id)
              AND e.Id = '{entryId}'
            """));

        // The prescribed repair, and the proof that the FLAG is what the entity reads:
        // every other column is unchanged, and the row now denies everyone.
        await ExecuteNonQueryAsync($"""
            UPDATE PageMarkings SET IsUnavailable = 1 WHERE PageId = '{pageId}';
            UPDATE PageEntries SET IsUnavailable = 1 WHERE Id = '{entryId}';
            """);

        using var fresh = CreateContext();
        var markingAfter = (await fresh.Set<PageMarking>()
            .Include(m => m.Countries).Include(m => m.Selectors)
            .SingleAsync(m => m.PageId == pageId)).ToMarking();
        Assert.True(markingAfter.IsUnavailable);
        Assert.Equal(ProtectiveMarking.FailClosed, markingAfter);

        var entryAfter = (await fresh.Set<PageEntry>()
            .Include(e => e.Countries)
            .SingleAsync(e => e.Id == entryId)).ToMarking();
        Assert.True(entryAfter.IsUnavailable);
        Assert.Equal(ProtectiveMarking.FailClosed, entryAfter);
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

            INSERT INTO Spaces (Id, [Key], Name, OriginInstanceId, IsExported, IsDeleted, LastOutboxSequence, CreatedAtUtc, CreatedByUserId)
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

    /// <summary>
    /// BinaryCollationOnStringKeys. Every string used as a LOOKUP KEY must mean the same
    /// thing on both providers, and five of them did not: SQL Server's default collation
    /// is case-INsensitive while SQLite's is case-sensitive for ASCII, so "ENG" and "eng"
    /// were one space in production and two in the tier that runs on every commit — with
    /// the looser rule being the tested one.
    ///
    /// <para>Structurally uncatchable below this tier, which is the whole point: SQLite
    /// is already case-sensitive, so the *absence* of a collation looks identical to its
    /// presence there. Only a real engine can tell. Both halves are asserted — the
    /// declared collation on the column, and the behaviour that follows from it, since a
    /// collation that landed on the column but not on its unique index would satisfy the
    /// first and not the second.</para>
    /// </summary>
    [SqlServerFact]
    public async Task StringLookupKeys_AreBinaryCollated_SoCaseMeansTheSameThingOnBothProviders()
    {
        foreach (var (table, column) in new[]
        {
            ("Spaces", "Key"),
            ("Pages", "Slug"),
            ("Labels", "Name"),
            ("KnownGroups", "Name"),
            ("AttributeDefinitions", "Key"),
        })
        {
            Assert.Equal(
                "Latin1_General_100_BIN2",
                await ExecuteScalarAsync<string>(
                    $"SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name = '{column}'"));
        }

        // The behaviour the two migrations produce TOGETHER, which is the part worth
        // pinning: BIN2 makes the engine compare ordinally, and canonical storage
        // (RocketWikiDbContext) makes every stored value one case — so a case-sensitive
        // unique index enforces case-INsensitive uniqueness. Writing "eng" beside "ENG"
        // is therefore not two spaces here and one on SQL Server, as it used to be; it is
        // the same space on both, and the second write collides.
        using var context = CreateContext();
        var actor = Guid.NewGuid();
        context.Users.Add(new User
        {
            Id = actor, Subject = $"collation-{actor}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        });
        context.Spaces.Add(new Space
        {
            Key = "eng", Name = "Lower on the way in", OriginInstanceId = "local-instance",
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = actor,
        });
        await context.SaveChangesAsync();

        // Stored canonically regardless of what the caller wrote.
        Assert.Equal(1, await ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Spaces WHERE [Key] = 'ENG'"));
        Assert.Equal(0, await ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Spaces WHERE [Key] = 'eng'"));

        using var second = CreateContext();
        second.Spaces.Add(new Space
        {
            Key = "EnG", Name = "A third casing", OriginInstanceId = "local-instance",
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = actor,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// The URL round trip on a real engine (design.md §17): a page created with a
    /// hand-edited mixed-case slug stores lower-case and is addressable in any casing,
    /// in the same space addressed in any casing.
    ///
    /// <para>This tier specifically, because slugs and keys are exactly the class where
    /// the two providers used to disagree — SQL Server folded case in its default
    /// collation and SQLite did not, so a SQLite-only proof of case-insensitivity would
    /// prove nothing about production, and before BinaryCollationOnStringKeys it would
    /// have passed for the wrong reason (the engine folding, not the application
    /// canonicalizing).</para>
    /// </summary>
    [SqlServerFact]
    public async Task PageAddress_IsCaseInsensitive_OverTheRealEngine()
    {
        using var context = CreateContext();
        var actor = new User
        {
            Subject = $"slug-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        context.Users.Add(actor);
        await context.SaveChangesAsync();

        var space = (await new SpaceService(context, "local-instance").CreateAsync(
            new CreateSpaceRequest("mIxEd", "Mixed", null),
            [
                new InitialGrant(AccessRuleKind.RoleGrant, SpaceRole.SpaceAdmin, """{ "everyone": true }"""),
                new InitialGrant(AccessRuleKind.AccessGrant, null, """{ "everyone": true }"""),
            ],
            isInstanceAdmin: true, actor.Id, AuditCtx)).Value;
        Assert.Equal("MIXED", space.Key);

        var page = (await new PageService(context, "local-instance").CreatePageAsync(
            new CreatePageRequest(space.Id, null, "My-Runbook", "My Runbook", "# Runbook"),
            Principal.Create("seed-sub", []), actor.Id, AuditCtx)).Value;
        Assert.Equal("my-runbook", page.Slug);

        // Every casing of the address resolves the same page - including the one the
        // author actually typed, which is no longer the stored form.
        var reads = new PageReadService(context);
        foreach (var (key, slug) in new[]
        {
            ("MIXED", "my-runbook"), ("mixed", "MY-RUNBOOK"), ("mIxEd", "My-Runbook"),
        })
        {
            Assert.Equal(page.Id, await reads.FindPageIdBySlugAsync(key, slug));
        }

        // And slug uniqueness is case-insensitive with it: the same address in another
        // casing is refused as taken, not accepted as a second page.
        var duplicate = await new PageService(context, "local-instance").CreatePageAsync(
            new CreatePageRequest(space.Id, null, "MY-RUNBOOK", "Clash", "# Clash"),
            Principal.Create("seed-sub", []), actor.Id, AuditCtx);
        Assert.False(duplicate.IsSuccess);
        Assert.IsType<ValidationError>(duplicate.Error);
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
        // Slug uniqueness is per SPACE now, not per parent: the slug is a page's
        // address (/spaces/{key}/{slug}) with the hierarchy deliberately left out, so a
        // page keeps its URL when it moves. The old per-parent index must be GONE, not
        // merely joined by a new one — two overlapping uniqueness rules would let a
        // move silently fail on a constraint nobody remembered.
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Pages_Space_Slug' AND has_filter = 1 AND is_unique = 1"));
        // AddPageEntries: the collection index is filtered, and - the part that only a
        // real SQL Server can check - the lookup column really is binary-collated. SQLite
        // cannot catch a missing collation here because its own default is already
        // case-sensitive, which is exactly the asymmetry the collation exists to remove.
        Assert.Equal(1, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_PageEntries_Page_Collection' AND has_filter = 1"));
        Assert.Equal("Latin1_General_100_BIN2", await ExecuteScalarAsync<string>(
            "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID('PageEntries') AND name = 'Collection'"));
        Assert.Equal(0, await ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Pages_Space_Parent_Slug'"));
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

    /// <summary>The helpers below default to the base class's fully-migrated database;
    /// a test that migrates its own fresh database to an older step and back passes that
    /// database's connection string, so its seed and its assertions look at the same
    /// rows. Plain SqlCommand rather than <c>ExecuteSqlRaw</c>, because the latter runs
    /// the text through <c>string.Format</c> and a JSON literal's braces break it.</summary>
    private async Task ExecuteNonQueryAsync(string sql, string? connectionString = null)
    {
        await using var connection = new SqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ExecuteScalarAsync<T>(string sql, string? connectionString = null)
    {
        await using var connection = new SqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private async Task<List<string>> ExecuteColumnAsync(string sql, string? connectionString = null)
    {
        await using var connection = new SqlConnection(connectionString ?? ConnectionString);
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
