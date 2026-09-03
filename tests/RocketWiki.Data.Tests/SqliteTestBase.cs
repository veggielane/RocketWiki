using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Tests.Access;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §14: EF Core on SQLite, fresh schema per fixture. xUnit constructs a new
/// instance of the test class for every [Fact]/[Theory] case, so deriving from this base
/// gives each test its own in-memory database with no state leaking between tests - that
/// matters here because several tests deliberately trigger unique/check-constraint
/// violations that would otherwise poison later tests sharing the same connection.
///
/// Where SQLite's behaviour genuinely differs from SQL Server (read this before trusting
/// a green run here as full coverage):
/// - SQLite does not enforce foreign keys unless a connection turns it on; this base does
///   so explicitly (`PRAGMA foreign_keys = ON`), otherwise the NO ACTION delete-restriction
///   tests would silently pass for the wrong reason (no enforcement at all, not correct
///   enforcement).
/// - Schema here is built from EF's live C# model via EnsureCreated, not by replaying the
///   checked-in migration. That proves the *model* behaves correctly against a real
///   database engine; it does not re-verify the migration file's SQL, which was reviewed
///   by hand separately.
/// - SQLite has only TEXT/INTEGER/REAL/BLOB/NUMERIC storage classes with dynamic per-value
///   typing - declared lengths (nvarchar(500), binary(32), etc.) are not enforced the way
///   SQL Server would enforce them. A string longer than its HasMaxLength will happily
///   insert here; that is a SQLite limitation, not evidence the constraint is unnecessary.
/// - CHECK constraints and filtered (partial) indexes, by contrast, ARE genuinely enforced
///   by SQLite and are exercised for real in these tests.
/// - No full-text search, no native `vector` column type, no partitioning, no SQL-login
///   grants - none of that is reachable from SQLite. Those stay covered only by the
///   migration's TODO comments pending a SQL Server-backed test tier (design.md §14's
///   third tier, Testcontainers-based, out of this project's scope).
/// </summary>
public abstract class SqliteTestBase : IDisposable
{
    private readonly SqliteConnection _connection;

    protected SqliteTestBase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    /// <summary>
    /// The instance id stamped into every context <see cref="CreateContext()"/> hands
    /// out (design.md §12; the sync outbox writer refuses to journal an exported
    /// space when the context has none). Matches TestData.NewSpace()'s
    /// OriginInstanceId so a seeded space is native by default; fixtures that model a
    /// different instance (BundleExportImportTests' "low side") override this.
    /// </summary>
    protected virtual string DefaultLocalInstanceId => "local-instance";

    /// <summary>
    /// The selector catalog stamped into every context (design.md §21.15) — the shared
    /// test vocabulary (<c>FRUIT</c> gated by <c>fruit</c>, <c>REGION</c> gated by nobody),
    /// so a scenario reads the same here as in the Core and API tiers. Override with
    /// <see cref="SelectorCatalog.Empty"/> to model an instance that configured nothing.
    /// </summary>
    protected virtual SelectorCatalog DefaultSelectorCatalog => TestCatalogs.Fruit;

    protected RocketWikiDbContext CreateContext() => CreateContext(DefaultLocalInstanceId);

    /// <summary>Pass null to build a context with NO local instance id configured —
    /// only for tests proving the outbox writer's fail-closed reaction to exactly
    /// that misconfiguration.</summary>
    protected RocketWikiDbContext CreateContext(string? localInstanceId)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlite(_connection)
            .UseSelectorCatalog(DefaultSelectorCatalog);
        if (localInstanceId is not null)
        {
            optionsBuilder.UseLocalInstanceId(localInstanceId);
        }

        return new RocketWikiDbContext(optionsBuilder.Options);
    }

    public void Dispose() => _connection.Dispose();
}
