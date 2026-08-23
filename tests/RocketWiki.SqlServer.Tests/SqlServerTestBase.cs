using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// The SQL Server twin of RocketWiki.Data.Tests' SqliteTestBase: xUnit constructs a
/// new test-class instance per [SqlServerFact], and each instance gets its OWN
/// database (unique name on the shared container) so no state leaks between tests -
/// the same isolation contract the SQLite tier gets from a fresh in-memory file.
///
/// The one deliberate difference: schema comes from <c>Database.Migrate()</c> - the
/// real, checked-in migrations - not EnsureCreated. The SQLite tier proves the live
/// EF *model*; this tier's entire reason to exist (design.md §14 tier 3) is proving
/// the migration SQL and provider-specific behavior (FTS, IDENTITY-in-composite-key,
/// enforced lengths, real transactions) that EnsureCreated would bypass. It also
/// means every derived test runs against the same schema production migrates to.
///
/// Derived classes must take <see cref="SqlServerContainerFixture"/> as a constructor
/// parameter (xUnit injects the collection fixture) and mark tests [SqlServerFact].
/// </summary>
[Collection(SqlServerCollection.Name)]
public abstract class SqlServerTestBase
{
    protected SqlServerTestBase(SqlServerContainerFixture fixture)
    {
        ConnectionString = fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());

        using var context = CreateContext();
        context.Database.Migrate();
    }

    protected string ConnectionString { get; }

    /// <summary>Matches TestData.NewSpace()'s OriginInstanceId, same as SqliteTestBase.</summary>
    protected virtual string DefaultLocalInstanceId => "local-instance";

    protected RocketWikiDbContext CreateContext() => CreateContext(DefaultLocalInstanceId);

    protected RocketWikiDbContext CreateContext(string? localInstanceId)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlServer(ConnectionString);
        if (localInstanceId is not null)
        {
            optionsBuilder.UseLocalInstanceId(localInstanceId);
        }

        return new RocketWikiDbContext(optionsBuilder.Options);
    }
}
