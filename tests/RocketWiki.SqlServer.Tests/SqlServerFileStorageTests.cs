using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RocketWiki.Storage;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// SqlServerFileStorage against the real engine (design.md §14 tier 3) — the only place
/// its behavior can honestly be proven, because everything interesting about it is
/// dialect: <c>varbinary(max)</c> chunked with <c>.WRITE</c>, <c>SequentialAccess</c>
/// reads that stream instead of buffering, and a binary-collation primary key.
///
/// Deliberately NOT derived from <see cref="SqlServerTestBase"/>, which would hand over a
/// migrated database: this provider takes nothing from the EF migration chain and creates
/// its own table on first use (design.md §10), so the databases here are created empty and
/// stay that way apart from the one table the provider makes. A migrated base class would
/// hide precisely the claim under test — and <see cref="ProviderNeedsNoMigrations"/>
/// asserts it outright.
///
/// The class shares one empty database across its tests (keys are unique per test); the
/// two tests that need a virgin database make their own.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerFileStorageTests
{
    /// <summary>Static: one database for the whole class, created by whichever test runs
    /// first. Tests in a collection run sequentially, so the check-then-create below needs
    /// no more care than this.</summary>
    private static readonly string SharedDatabaseName = SqlServerContainerFixture.NewDatabaseName();

    private const int LargeBlobBytes = 9 * 1024 * 1024;

    private readonly SqlServerContainerFixture _fixture;
    private readonly string _connectionString;

    public SqlServerFileStorageTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
        _connectionString = CreateDatabase(fixture, SharedDatabaseName);
    }

    [SqlServerFact]
    public async Task SaveThenOpenRead_RoundTripsBytesExactly()
    {
        var storage = CreateStorage();
        var expected = Pattern(4096);
        const string key = "attachments/2026/08/round-trip.bin";

        await storage.SaveAsync(key, new MemoryStream(expected), "application/octet-stream", default);

        await using var stream = await storage.OpenReadAsync(key, default);
        AssertBytesEqual(expected, await ReadAllAsync(stream));
    }

    [SqlServerFact]
    public async Task LargeBlob_SurvivesTheChunkedWriteAndTheSequentialRead()
    {
        // Comfortably over the provider's 1 MiB chunk, so the write loops nine times and
        // the read comes back across several TDS packets rather than in one buffer. The
        // point of both loops is that memory stays bounded by the chunk, not the
        // attachment - design.md §10 allows 100 MiB uploads.
        var storage = CreateStorage();
        var expected = Pattern(LargeBlobBytes);
        const string key = "attachments/2026/08/large.bin";

        await storage.SaveAsync(key, new MemoryStream(expected), "application/octet-stream", default);

        await using var stream = await storage.OpenReadAsync(key, default);

        // A sequential reader stream, not a materialized buffer: if the provider ever
        // regressed to loading the row into memory this would start reporting a length.
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Length);

        AssertBytesEqual(expected, await ReadAllAsync(stream));
    }

    [SqlServerFact]
    public async Task OpenRead_DoesNotMaterializeTheRow_ItStreamsIt()
    {
        // The claim "reads stream" made measurable. CommandBehavior.SequentialAccess is
        // what makes SqlDataReader.GetStream hand back a sequential reader over the TDS
        // packets; drop it and GetStream returns a MemoryStream over the whole value,
        // which for design.md §10's 100 MiB ceiling is a 100 MiB allocation per concurrent
        // download. Allocation is the only observable difference, and it is a stark one:
        // opening a 9 MiB blob and reading 16 bytes of it must cost a fraction of the row.
        var storage = CreateStorage();
        const string key = "attachments/2026/08/streamed-read.bin";
        await storage.SaveAsync(key, new MemoryStream(Pattern(LargeBlobBytes)), "application/octet-stream", default);

        var before = GC.GetTotalAllocatedBytes(precise: true);

        await using (var stream = await storage.OpenReadAsync(key, default))
        {
            var head = new byte[16];
            await stream.ReadExactlyAsync(head, default);
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        // Deliberately loose: this is a "did the whole row land in memory" tripwire, not an
        // allocation budget. A buffering regression allocates at least the 9 MiB row.
        Assert.True(allocated < 4 * 1024 * 1024,
            $"Opening and partially reading a {LargeBlobBytes:N0}-byte blob allocated {allocated:N0} bytes - "
            + "the row is being materialized instead of streamed (CommandBehavior.SequentialAccess).");
    }

    [SqlServerFact]
    public async Task Save_DoesNotMaterializeTheSource_ItChunksIt()
    {
        // The write half of the same claim. The chunk loop's whole purpose is that memory
        // is bounded by the chunk size, not the attachment size; reading the source into a
        // byte[] to hand SqlClient one parameter would be simpler and would allocate the
        // entire attachment. The pooled buffer is rented once per process, so even the
        // first save here stays under the bar.
        var storage = CreateStorage();
        var source = new MemoryStream(Pattern(LargeBlobBytes));

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await storage.SaveAsync("attachments/2026/08/streamed-write.bin", source, "application/octet-stream", default);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(allocated < 4 * 1024 * 1024,
            $"Saving a {LargeBlobBytes:N0}-byte stream allocated {allocated:N0} bytes - the source is being "
            + "materialized instead of appended a chunk at a time.");
    }

    [SqlServerFact]
    public async Task SaveOverAnExistingKey_ReplacesTheBytesRatherThanAppending()
    {
        // design.md §10's upload order writes bytes first, so a retried upload under the
        // same key must overwrite - including shrinking, which an append-only .WRITE loop
        // without the empty seed would get wrong in the most silent possible way.
        var storage = CreateStorage();
        const string key = "attachments/2026/08/overwrite.bin";
        var second = Pattern(11);

        await storage.SaveAsync(key, new MemoryStream(Pattern(3 * 1024 * 1024)), "application/octet-stream", default);
        await storage.SaveAsync(key, new MemoryStream(second), "text/plain", default);

        await using var stream = await storage.OpenReadAsync(key, default);
        AssertBytesEqual(second, await ReadAllAsync(stream));
    }

    [SqlServerFact]
    public async Task EmptyStream_StoresZeroBytesRatherThanFailing()
    {
        var storage = CreateStorage();
        const string key = "attachments/2026/08/empty.bin";

        await storage.SaveAsync(key, new MemoryStream([]), "application/octet-stream", default);

        Assert.True(await storage.ExistsAsync(key, default));
        await using var stream = await storage.OpenReadAsync(key, default);
        Assert.Empty(await ReadAllAsync(stream));
    }

    [SqlServerFact]
    public async Task OpenRead_MissingKey_ThrowsFileNotFound()
    {
        // The exception type the sibling providers throw, because AttachmentReadService
        // and the janitor path (design.md §10) are written against one contract.
        var storage = CreateStorage();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => storage.OpenReadAsync("attachments/2026/08/absent.bin", default));
    }

    [SqlServerFact]
    public async Task Delete_RemovesTheBlob_AndDeletingWhatIsNotThereIsANoOp()
    {
        var storage = CreateStorage();
        const string key = "attachments/2026/08/deleted.bin";

        await storage.SaveAsync(key, new MemoryStream(Pattern(64)), "application/octet-stream", default);
        await storage.DeleteAsync(key, default);
        Assert.False(await storage.ExistsAsync(key, default));

        // Second delete: silent, like the filesystem and S3 providers - the janitor sweeps
        // orphans and must not care whether it lost a race with a real deletion.
        await storage.DeleteAsync(key, default);
        await storage.DeleteAsync("attachments/2026/08/never-existed.bin", default);
    }

    [SqlServerFact]
    public async Task Exists_AnswersWithoutTransferringTheBlob()
    {
        var storage = CreateStorage();
        const string key = "attachments/2026/08/exists.bin";

        Assert.False(await storage.ExistsAsync(key, default));
        await storage.SaveAsync(key, new MemoryStream(Pattern(LargeBlobBytes)), "application/octet-stream", default);
        Assert.True(await storage.ExistsAsync(key, default));
    }

    [SqlServerFact]
    public async Task KeysDifferingOnlyInCase_AreDistinctObjects()
    {
        // The key column carries an explicit binary collation: a database created with the
        // usual case-insensitive default would otherwise fold these two into one row, while
        // S3 and any case-sensitive filesystem keep them apart. Providers must agree on
        // what "same key" means.
        var storage = CreateStorage();
        var lower = Pattern(32);
        var upper = Pattern(64);

        await storage.SaveAsync("attachments/2026/08/case-a.bin", new MemoryStream(lower), "application/octet-stream", default);
        await storage.SaveAsync("attachments/2026/08/CASE-A.bin", new MemoryStream(upper), "application/octet-stream", default);

        await using var lowerStream = await storage.OpenReadAsync("attachments/2026/08/case-a.bin", default);
        AssertBytesEqual(lower, await ReadAllAsync(lowerStream));

        await using var upperStream = await storage.OpenReadAsync("attachments/2026/08/CASE-A.bin", default);
        AssertBytesEqual(upper, await ReadAllAsync(upperStream));
    }

    [SqlServerFact]
    public async Task ReadStream_ReleasesItsConnectionOnDispose_EvenWhenAbandonedMidStream()
    {
        // The API returns this stream from the request handler and pipes it to the
        // response, so the reader, command and connection outlive OpenReadAsync and are
        // released only on dispose. Max Pool Size=2 turns a leak into a fast, obvious
        // failure: the third undisposed download would time out waiting for the pool.
        var connectionString = new SqlConnectionStringBuilder(_connectionString)
        {
            MaxPoolSize = 2,
            ConnectTimeout = 5,
        }.ConnectionString;

        var storage = CreateStorage(connectionString);
        const string key = "attachments/2026/08/pooled.bin";
        var expected = Pattern(2 * 1024 * 1024);
        await storage.SaveAsync(key, new MemoryStream(expected), "application/octet-stream", default);

        for (var i = 0; i < 10; i++)
        {
            await using var stream = await storage.OpenReadAsync(key, default);

            if (i % 2 == 0)
            {
                AssertBytesEqual(expected, await ReadAllAsync(stream));
            }
            else
            {
                // The abandoned-download case: a client that disconnects after the first
                // few bytes must still give the connection back.
                var head = new byte[16];
                await stream.ReadExactlyAsync(head, default);
            }
        }
    }

    [SqlServerFact]
    public async Task FirstUse_CreatesItsTableUnderConcurrentStartupWithoutRacing()
    {
        // A virgin database and eight storages, each with its own "have I created the
        // table yet" flag, all reaching for it at once - the shape of several hosts (or
        // several worker threads on one host) taking their first upload after a deploy.
        // The IF OBJECT_ID check alone cannot make that safe; losing the race must be
        // survivable, not merely unlikely.
        var connectionString = CreateDatabase(_fixture, SqlServerContainerFixture.NewDatabaseName());

        var saves = Enumerable.Range(0, 8).Select(async i =>
        {
            var storage = CreateStorage(connectionString);
            await storage.SaveAsync(
                $"attachments/2026/08/concurrent-{i}.bin", new MemoryStream(Pattern(1024)), "application/octet-stream", default);
        });

        await Task.WhenAll(saves);

        var verifier = CreateStorage(connectionString);
        for (var i = 0; i < 8; i++)
        {
            Assert.True(await verifier.ExistsAsync($"attachments/2026/08/concurrent-{i}.bin", default));
        }
    }

    [SqlServerFact]
    public async Task ProviderNeedsNoMigrations()
    {
        // The database this class uses was created with CREATE DATABASE and nothing else:
        // no EF migration has ever run against it. The provider's table appears anyway,
        // which is the whole reason it stays outside the migration chain (design.md §10 -
        // an opt-in provider must not add a table to deployments that never select it).
        var connectionString = CreateDatabase(_fixture, SqlServerContainerFixture.NewDatabaseName());
        var storage = CreateStorage(connectionString);

        await storage.SaveAsync(
            "attachments/2026/08/no-migrations.bin", new MemoryStream(Pattern(128)), "text/plain", default);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.FileStorageBlobs', N'U'), OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U'), " +
            "(SELECT ByteLength FROM dbo.FileStorageBlobs WHERE [Key] = @key), " +
            "(SELECT ContentType FROM dbo.FileStorageBlobs WHERE [Key] = @key);",
            connection);
        command.Parameters.AddWithValue("@key", "attachments/2026/08/no-migrations.bin");

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.False(await reader.IsDBNullAsync(0));
        Assert.True(await reader.IsDBNullAsync(1));
        Assert.Equal(128L, reader.GetInt64(2));
        Assert.Equal("text/plain", reader.GetString(3));
    }

    private SqlServerFileStorage CreateStorage(string? connectionString = null) => new(
        Options.Create(new FileStorageOptions
        {
            SqlServer = new SqlServerFileStorageOptions { ConnectionString = connectionString ?? _connectionString },
        }),
        new ConfigurationBuilder().Build());

    /// <summary>
    /// An empty database on the shared container - no schema, no migration history. CREATE
    /// DATABASE must be alone in its batch, hence the EXEC; the name is generated hex, so
    /// there is nothing to inject.
    /// </summary>
    private static string CreateDatabase(SqlServerContainerFixture fixture, string databaseName)
    {
        using (var master = new SqlConnection(fixture.Container.GetConnectionString()))
        {
            master.Open();
            using var command = new SqlCommand(
                $"IF DB_ID(N'{databaseName}') IS NULL EXEC('CREATE DATABASE [{databaseName}]');", master);
            command.ExecuteNonQuery();
        }

        return fixture.CreateConnectionString(databaseName);
    }

    /// <summary>Deterministic bytes: a failure has to be reproducible, and a repeating
    /// pattern could hide a chunk written twice or in the wrong order.</summary>
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        new Random(20260824).NextBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>SequenceEqual rather than Assert.Equal: comparing multi-megabyte arrays
    /// element-wise through xUnit's formatter is slow and its failure output unreadable.</summary>
    private static void AssertBytesEqual(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "Stored bytes differ from the bytes written.");
    }
}
