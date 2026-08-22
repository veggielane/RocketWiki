using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace RocketWiki.Data;

/// <summary>
/// SQLite's rowid-alias autoincrement mechanism only applies when an INTEGER column is
/// the SOLE primary key. AuditEvent's PK is composite (TimestampUtc, Id) by design
/// (data-model.md - it needs TimestampUtc in the key for SQL Server's clustered
/// partitioning), so SQLite has no native way to auto-generate Id there. SQL Server does
/// not share this limitation: IDENTITY is a column-level property independent of key
/// structure, confirmed by the generated migration marking Id with
/// `.Annotation("SqlServer:Identity", "1, 1")` despite being part of the composite PK.
///
/// This generator exists purely so the SQLite-backed test tier (and any other non-SQL
/// Server use of this model) can exercise inserts into AuditEvent at all - see
/// RocketWikiDbContext.OnModelCreating, where it is wired up only when the active
/// provider is SQLite. Production on SQL Server keeps native IDENTITY, exactly as
/// data-model.md specifies.
/// </summary>
internal sealed class SqliteAuditEventIdGenerator : ValueGenerator<long>
{
    // Ticks-seeded so values stay large/monotonic-looking across process restarts;
    // Interlocked.Increment guarantees no collision within this process, which is all a
    // single SQLite connection ever needs.
    private static long _lastValue = DateTime.UtcNow.Ticks;

    public override bool GeneratesTemporaryValues => false;

    public override long Next(EntityEntry entry) => Interlocked.Increment(ref _lastValue);
}
