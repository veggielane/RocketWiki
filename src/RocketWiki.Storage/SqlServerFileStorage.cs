using System.Buffers;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace RocketWiki.Storage;

/// <summary>
/// SQL Server-backed <see cref="IFileStorage"/> provider (design.md §10): blob bytes
/// live in rows of one table rather than in an object store or a directory.
///
/// **Not recommended as the production default, and selected only on purpose.** An
/// object store is where 100 MiB attachments belong: every byte written here passes
/// through the transaction log, enlarges every database backup, competes for the buffer
/// pool, is billed at database-storage rates, and has no CDN or offload path in front of
/// it. The provider exists because some deployments legitimately want those costs: a
/// single-container or air-gapped install where one database backup covers content *and*
/// blobs at the same point in time; a dev or test environment that would rather not run
/// a MinIO or mount a volume; restore drills that are one restore instead of two.
///
/// The table is created on first use, idempotently, and sits deliberately OUTSIDE the EF
/// Core migration chain. RocketWiki.Storage references neither EF Core nor
/// RocketWiki.Data — the dependency runs the other way (design.md §14's layout), and
/// inverting it for an opt-in provider would be the wrong trade. Nor may an opt-in
/// provider add a table to every production deployment that will never select it, or
/// perturb the checked-in migration set <c>MigrationTests</c> asserts. The DDL is
/// published in docs/CONFIGURATION.md: a deployment whose application account holds no
/// CREATE TABLE right can pre-create the table, and this provider then simply finds it.
///
/// Connection string: <c>FileStorage:SqlServer:ConnectionString</c>, falling back to the
/// application's <c>ConnectionStrings:rocketwiki</c>. Sharing the application database is
/// the point of the provider (one backup, one restore, one point in time); pointing it at
/// a separate database is supported and is often the wiser shape, since blob churn
/// otherwise lands in the same transaction log as page edits.
///
/// Bytes stream in both directions — a 100 MiB attachment is never held in memory.
/// Writes append <see cref="ChunkBytes"/> at a time through <c>UPDATE ... .WRITE</c> over
/// a pooled buffer; reads hand back a <c>SequentialAccess</c> reader stream that owns its
/// reader, command and connection and releases all three when the caller disposes it —
/// the API streams that stream straight to the response, so the lifetime belongs to the
/// caller, not to this method.
///
/// No presigned-URL member exists here, deliberately and for the same reason it does not
/// exist on <see cref="S3FileStorage"/>: bytes always flow through the API's own routes,
/// which enforce `canView` and write the §7 audit row before streaming (design.md §7,
/// §10). Every method validates its key via <see cref="StorageKey"/> before a connection
/// is opened, and keys travel as parameters — never inlined into a statement — so
/// SqlClient's own instrumentation cannot carry one onto a span even with statement
/// capture enabled (design.md §15).
/// </summary>
public sealed class SqlServerFileStorage : IFileStorage
{
    /// <summary>Connection string the API host binds its DbContext to (Program.cs), and
    /// therefore the fallback that makes "blobs in the application database" the
    /// zero-configuration shape of this provider.</summary>
    internal const string ApplicationConnectionStringName = "rocketwiki";

    internal const string TableName = "dbo.FileStorageBlobs";

    /// <summary>
    /// Bytes appended per <c>.WRITE</c> round trip. The whole point of the loop is that
    /// this — not the attachment size — bounds the memory a save costs, so it is a
    /// deliberate trade of round trips against working set: 1 MiB is 100 round trips for
    /// a 100 MiB attachment (design.md §10's cap) and 1 MiB of pooled buffer per
    /// concurrent upload. It is also the shared <see cref="ArrayPool{T}"/>'s largest
    /// bucket, so <c>Rent</c> hands back an exactly-sized array rather than an oversized
    /// one.
    /// </summary>
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>Width of Attachments.StorageKey and its siblings (data-model.md), so a
    /// key this table cannot hold is a key the row naming it could not hold either.</summary>
    private const int KeyColumnLength = 200;

    /// <summary>Width of Attachments.ContentType (data-model.md).</summary>
    private const int ContentTypeColumnLength = 127;

    /// <summary>
    /// Declared width of the key parameter — wider than the column on purpose. A
    /// parameter sized to the column would silently truncate an over-long key and make it
    /// collide with its own prefix; sized wider, an over-long key fails loudly on insert
    /// instead. Fixed rather than inferred from each value so every call reuses one plan.
    /// </summary>
    private const int KeyParameterLength = 4000;

    /// <summary>"There is already an object named ... in the database." Two hosts can
    /// both pass the OBJECT_ID check and both issue the CREATE; the loser sees this, and
    /// the table exists either way.</summary>
    private const int ObjectAlreadyExistsError = 2714;

    private const int DuplicateKeyError = 2627;

    /// <summary>
    /// <c>Latin1_General_100_BIN2</c> on the key column, not the database default:
    /// database defaults are case-insensitive (<c>..._CI_AS</c>), which would make
    /// <c>attachments/a</c> and <c>attachments/A</c> the same object here and different
    /// objects on S3 and on any case-sensitive filesystem. A binary collation makes the
    /// primary-key lookup ordinal, so all three providers agree on what "same key" means.
    /// A parameter carries the database's default collation, but a column reference wins
    /// collation precedence, so every comparison below is ordinal without saying so.
    /// </summary>
    private static readonly string CreateTableSql = $"""
        IF OBJECT_ID(N'{TableName}', N'U') IS NULL
        BEGIN
            CREATE TABLE {TableName}
            (
                [Key] nvarchar({KeyColumnLength}) COLLATE Latin1_General_100_BIN2 NOT NULL,
                [Content] varbinary(max) NOT NULL,
                [ContentType] nvarchar({ContentTypeColumnLength}) NULL,
                [ByteLength] bigint NOT NULL,
                [CreatedAtUtc] datetime2(3) NOT NULL,
                CONSTRAINT [PK_FileStorageBlobs] PRIMARY KEY CLUSTERED ([Key])
            );
        END
        """;

    /// <summary>
    /// Upsert of an EMPTY value, which the chunk loop then appends to. Two statements
    /// rather than MERGE (whose concurrency behavior needs more care than it saves), and
    /// resetting to <c>0x</c> rather than leaving the old bytes in place because a
    /// re-upload under an existing key must overwrite, not append — design.md §10's
    /// upload order, and what FileSystemFileStorage's <c>FileMode.Create</c> does.
    /// <c>.WRITE</c> also cannot append to a NULL value, so the empty seed is required,
    /// not merely tidy.
    /// </summary>
    private static readonly string ResetSql = $"""
        UPDATE {TableName}
           SET [Content] = 0x, [ContentType] = @contentType, [ByteLength] = 0, [CreatedAtUtc] = @createdAtUtc
         WHERE [Key] = @key;
        IF @@ROWCOUNT = 0
            INSERT INTO {TableName} ([Key], [Content], [ContentType], [ByteLength], [CreatedAtUtc])
            VALUES (@key, 0x, @contentType, 0, @createdAtUtc);
        """;

    /// <summary>@Offset NULL means "append at the end", and @Length is then ignored.</summary>
    private static readonly string AppendChunkSql =
        $"UPDATE {TableName} SET [Content].WRITE(@chunk, NULL, 0) WHERE [Key] = @key;";

    private static readonly string SetByteLengthSql =
        $"UPDATE {TableName} SET [ByteLength] = @byteLength WHERE [Key] = @key;";

    /// <summary>
    /// DATALENGTH first, then the blob: <c>SequentialAccess</c> requires columns to be
    /// read in order, and the length has to be in hand before the stream is handed out.
    /// It is computed rather than read from [ByteLength] so that a torn write cannot make
    /// the telemetry claim more bytes than the reader will actually produce.
    /// </summary>
    private static readonly string SelectContentSql =
        $"SELECT CAST(DATALENGTH([Content]) AS bigint), [Content] FROM {TableName} WHERE [Key] = @key;";

    /// <summary>Never SELECTs [Content]: existence must not transfer the blob.</summary>
    private static readonly string ExistsSql =
        $"SELECT 1 FROM {TableName} WHERE [Key] = @key;";

    private static readonly string DeleteSql =
        $"DELETE FROM {TableName} WHERE [Key] = @key;";

    private readonly SemaphoreSlim _tableGate = new(1, 1);
    private volatile bool _tableReady;

    public SqlServerFileStorage(IOptions<FileStorageOptions> options, IConfiguration configuration)
    {
        var configured = options.Value.SqlServer?.ConnectionString;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = configuration.GetConnectionString(ApplicationConnectionStringName);
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "FileStorage:SqlServer:ConnectionString must be configured when using the SqlServer provider, " +
                $"or ConnectionStrings:{ApplicationConnectionStringName} must be set for it to fall back to " +
                "(sharing the application database is this provider's default shape).");
        }

        ConnectionString = configured;
    }

    /// <summary>
    /// Internal, not public: which connection string won the fallback is a fact tests
    /// assert, not part of the provider's contract — and the §10 tripwire pins the public
    /// surface. Never logged or tagged; it carries credentials.
    /// </summary>
    internal string ConnectionString { get; }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.SqlServerProvider, StorageTelemetry.SaveOperation);
        try
        {
            StorageKey.Validate(key);
            await using var connection = await OpenConnectionAsync(ct);

            await ResetToEmptyAsync(connection, key, contentType, ct);

            var totalBytes = 0L;
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                await using var append = new SqlCommand(AppendChunkSql, connection);
                append.Parameters.Add(KeyParameter(key));
                var chunk = append.Parameters.Add("@chunk", SqlDbType.VarBinary, -1);

                while (true)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(0, ChunkBytes), ct);
                    if (read == 0)
                    {
                        break;
                    }

                    // Both halves of "send exactly `read` bytes": Size bounds what
                    // SqlClient transmits, and the value is exactly that long whenever the
                    // pool hands back an oversized array. Neither alone is worth relying
                    // on, and together they cost nothing on the common path — the shared
                    // pool's 1 MiB bucket is exact, so a full chunk goes out without a copy.
                    chunk.Size = read;
                    chunk.Value = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();

                    await append.ExecuteNonQueryAsync(ct);
                    totalBytes += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await using (var setLength = new SqlCommand(SetByteLengthSql, connection))
            {
                setLength.Parameters.Add(KeyParameter(key));
                setLength.Parameters.Add("@byteLength", SqlDbType.BigInt).Value = totalBytes;
                await setLength.ExecuteNonQueryAsync(ct);
            }

            // Measured from what was actually written, not from the source stream's
            // Length, which a non-seekable upload stream doesn't have.
            operation.Bytes = totalBytes;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.SqlServerProvider, StorageTelemetry.OpenReadOperation);

        SqlConnection? connection = null;
        SqlCommand? command = null;
        SqlDataReader? reader = null;
        var handedOff = false;

        try
        {
            StorageKey.Validate(key);
            connection = await OpenConnectionAsync(ct);

            command = new SqlCommand(SelectContentSql, connection);
            command.Parameters.Add(KeyParameter(key));

            // SequentialAccess is what makes this a stream rather than a download: without
            // it SqlClient buffers the whole varbinary(max) value into memory before the
            // first read, which for a 100 MiB attachment is precisely the failure mode
            // this provider is written to avoid.
            reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

            if (!await reader.ReadAsync(ct))
            {
                operation.SetOutcome("not_found");
                throw new FileNotFoundException($"No object exists for key '{key}'.");
            }

            operation.Bytes = reader.GetInt64(0);

            var blob = new BlobStream(reader.GetStream(1), reader, command, connection);
            handedOff = true;
            return blob;
        }
        catch (FileNotFoundException)
        {
            // Outcome already recorded as not_found: a key with no row is an answer, not a
            // storage fault — same span shape S3FileStorage gives a 404 from the store.
            throw;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
        finally
        {
            if (!handedOff)
            {
                // Nothing was returned, so nothing else will ever close these.
                if (reader is not null)
                {
                    await reader.DisposeAsync();
                }

                if (command is not null)
                {
                    await command.DisposeAsync();
                }

                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.SqlServerProvider, StorageTelemetry.DeleteOperation);
        try
        {
            StorageKey.Validate(key);
            await using var connection = await OpenConnectionAsync(ct);
            await using var command = new SqlCommand(DeleteSql, connection);
            command.Parameters.Add(KeyParameter(key));

            // Deleting a key that isn't there is a no-op, not an error: the janitor
            // (design.md §10) and the two sibling providers all treat it that way.
            var deleted = await command.ExecuteNonQueryAsync(ct);
            if (deleted == 0)
            {
                operation.SetOutcome("not_found");
            }
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.SqlServerProvider, StorageTelemetry.ExistsOperation);
        try
        {
            StorageKey.Validate(key);
            await using var connection = await OpenConnectionAsync(ct);
            await using var command = new SqlCommand(ExistsSql, connection);
            command.Parameters.Add(KeyParameter(key));

            var exists = await command.ExecuteScalarAsync(ct) is not null;
            operation.SetOutcome(exists ? "found" : "not_found");
            return exists;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    private static SqlParameter KeyParameter(string key) =>
        new("@key", SqlDbType.NVarChar, KeyParameterLength) { Value = key };

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await EnsureTableAsync(connection, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// One DDL round trip per process, then a volatile-bool read forever after: the class
    /// is registered as a singleton, so the gate below is contended only during the first
    /// few operations after startup. The CREATE is idempotent twice over — the OBJECT_ID
    /// check for the ordinary case, and swallowing 2714 for the check-then-create race two
    /// hosts booting together can still lose.
    /// </summary>
    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken ct)
    {
        if (_tableReady)
        {
            return;
        }

        await _tableGate.WaitAsync(ct);
        try
        {
            if (_tableReady)
            {
                return;
            }

            await using var command = new SqlCommand(CreateTableSql, connection);
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number == ObjectAlreadyExistsError)
            {
                // Another host won the race. The table exists, which is all this promises.
            }

            _tableReady = true;
        }
        finally
        {
            _tableGate.Release();
        }
    }

    /// <summary>
    /// Storage keys are single-writer by construction (design.md §10: one opaque,
    /// system-generated key per upload), so the duplicate-key retry covers only the
    /// insert-versus-insert race the two-statement upsert leaves open — not two writers
    /// interleaving chunks into one key, which no provider defends against and none needs to.
    /// </summary>
    private async Task ResetToEmptyAsync(SqlConnection connection, string key, string contentType, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var command = new SqlCommand(ResetSql, connection);
            command.Parameters.Add(KeyParameter(key));

            // Informational only: Attachments.ContentType (data-model.md) is the source of
            // truth the API serves back, and this provider never reads this column. Bounded
            // to the row's own width, truncating rather than failing an upload over a copy.
            command.Parameters.Add("@contentType", SqlDbType.NVarChar, ContentTypeColumnLength).Value =
                (object?)contentType ?? DBNull.Value;
            command.Parameters.Add("@createdAtUtc", SqlDbType.DateTime2).Value = DateTime.UtcNow;

            try
            {
                await command.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (SqlException ex) when (ex.Number == DuplicateKeyError && attempt == 0)
            {
                // The row exists now, so the UPDATE half will hit it on the retry.
            }
        }
    }

    /// <summary>
    /// The reader's sequential-access stream plus ownership of everything it is served
    /// from. <see cref="OpenReadAsync"/> returns while the query is still open — the API
    /// pipes this straight into the response — so the reader, command and connection must
    /// outlive the call and be released exactly when the caller disposes the stream. A
    /// wrapper rather than <c>CommandBehavior.CloseConnection</c> because the command and
    /// the connection object still need disposing, and leaking either one exhausts the
    /// pool after a few hundred downloads.
    ///
    /// Not seekable, and does not report a Length: the bytes arrive as the engine sends
    /// them. S3FileStorage's response stream behaves the same way, so callers already
    /// cannot assume otherwise.
    /// </summary>
    private sealed class BlobStream : Stream
    {
        private readonly Stream _inner;
        private readonly SqlDataReader _reader;
        private readonly SqlCommand _command;
        private readonly SqlConnection _connection;
        private bool _disposed;

        public BlobStream(Stream inner, SqlDataReader reader, SqlCommand command, SqlConnection connection)
        {
            _inner = inner;
            _reader = reader;
            _command = command;
            _connection = connection;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (disposing)
            {
                _inner.Dispose();
                _reader.Dispose();
                _command.Dispose();
                _connection.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await _inner.DisposeAsync();
            await _reader.DisposeAsync();
            await _command.DisposeAsync();
            await _connection.DisposeAsync();

            GC.SuppressFinalize(this);
        }
    }
}
