namespace RocketWiki.Core.Content;

/// <summary>
/// Thrown by <see cref="BoundedReadStream"/> when a stream produces more bytes than its
/// caller was willing to accept. Carries the entry name so the refusal can say WHICH part
/// of an archive misbehaved, not merely that one did.
/// </summary>
public sealed class DecompressionLimitExceededException(string entryName, long maxBytes)
    : Exception(
        $"'{entryName}' expands past its {maxBytes}-byte ceiling. Either the archive declares a smaller size " +
        "for it than it actually contains, or the entry is genuinely larger than this importer accepts.")
{
    public string EntryName { get; } = entryName;

    public long MaxBytes { get; } = maxBytes;
}

/// <summary>
/// A read-only pass-through that refuses past a byte ceiling.
///
/// <para><b>Why a wrapper rather than a check after the fact.</b> Both places RocketWiki
/// ingests an archive from outside its trust boundary — a low→high sync bundle and a
/// Confluence space export — read zip entries, and a zip entry's uncompressed size is a
/// number the ARCHIVE declares. A few hundred kilobytes of compressed zeroes can claim to
/// be small and expand to tens of gigabytes, and by the time a post-hoc check could notice,
/// the bytes are already in the importing host's memory. So the count has to happen inside
/// the read, on the bytes that actually arrive.</para>
///
/// <para>Checking the declared size first is still worth doing (it is free, and it refuses
/// the honest-but-oversized case without reading anything) — but it is a fast path, never
/// the proof. This type is the proof.</para>
///
/// <para><b>Owns nothing.</b> Disposing it does not dispose the inner stream: both callers
/// hand it a <c>ZipArchiveEntry.Open()</c> stream they dispose themselves.</para>
/// </summary>
public sealed class BoundedReadStream(Stream inner, long maxBytes, string entryName) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(ReadCountedAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadCountedAsync(buffer.AsMemory(offset, count), cancellationToken);

    private async Task<int> ReadCountedAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    private int Count(int bytesRead)
    {
        _read += bytesRead;
        if (_read > maxBytes)
        {
            throw new DecompressionLimitExceededException(entryName, maxBytes);
        }

        return bytesRead;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Deliberately empty - see the class remarks. The inner stream belongs to the
        // ZipArchiveEntry that produced it.
    }
}
