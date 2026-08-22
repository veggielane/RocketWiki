using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    protected RocketWikiDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new RocketWikiDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}
