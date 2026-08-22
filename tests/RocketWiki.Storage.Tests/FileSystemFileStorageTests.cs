using System.Text;
using Microsoft.Extensions.Options;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// Real filesystem coverage for <see cref="FileSystemFileStorage"/> (design.md §10):
/// round-trip, overwrite, missing key, delete, exists, and — the compliance-relevant
/// part — that no storage key can escape the configured root. S3FileStorage is not
/// covered here; it needs a real S3-compatible endpoint (MinIO), which this suite
/// does not stand up. That leaves S3FileStorage's actual object-store behavior
/// (put/get/delete/metadata against a live bucket) unverified by this test project.
/// </summary>
public sealed class FileSystemFileStorageTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemFileStorage _storage;

    public FileSystemFileStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rocketwiki-storage-tests-" + Guid.NewGuid());
        _storage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
        {
            FileSystem = new FileSystemFileStorageOptions { Root = _root },
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Stream Utf8Stream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task SaveThenOpenRead_RoundTripsContent()
    {
        const string key = "attachments/2026/08/one.txt";

        await _storage.SaveAsync(key, Utf8Stream("hello rocketwiki"), "text/plain", CancellationToken.None);

        await using var read = await _storage.OpenReadAsync(key, CancellationToken.None);
        Assert.Equal("hello rocketwiki", await ReadAllAsync(read));
    }

    [Fact]
    public async Task Save_CreatesIntermediateDirectories()
    {
        const string key = "attachments/2026/08/nested/deep/file.bin";

        await _storage.SaveAsync(key, Utf8Stream("x"), "application/octet-stream", CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "attachments", "2026", "08", "nested", "deep", "file.bin")));
    }

    [Fact]
    public async Task Save_Overwrites_ExistingKey()
    {
        const string key = "attachments/2026/08/two.txt";

        await _storage.SaveAsync(key, Utf8Stream("version 1"), "text/plain", CancellationToken.None);
        await _storage.SaveAsync(key, Utf8Stream("version 2 (shorter tail)"), "text/plain", CancellationToken.None);

        await using var read = await _storage.OpenReadAsync(key, CancellationToken.None);
        Assert.Equal("version 2 (shorter tail)", await ReadAllAsync(read));
    }

    [Fact]
    public async Task OpenRead_MissingKey_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _storage.OpenReadAsync("attachments/2026/08/does-not-exist.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        const string key = "attachments/2026/08/three.txt";
        await _storage.SaveAsync(key, Utf8Stream("gone soon"), "text/plain", CancellationToken.None);

        await _storage.DeleteAsync(key, CancellationToken.None);

        Assert.False(await _storage.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_MissingKey_DoesNotThrow()
    {
        // Design.md §10: a missing storage object surfaces as a flagged error at
        // the Attachment-row level, not as an exception out of the storage layer
        // for an operation (delete) that is idempotent by nature.
        await _storage.DeleteAsync("attachments/2026/08/never-existed.txt", CancellationToken.None);
    }

    [Fact]
    public async Task Exists_TrueForSavedKey_FalseForUnknownKey()
    {
        const string key = "attachments/2026/08/four.txt";
        await _storage.SaveAsync(key, Utf8Stream("present"), "text/plain", CancellationToken.None);

        Assert.True(await _storage.ExistsAsync(key, CancellationToken.None));
        Assert.False(await _storage.ExistsAsync("attachments/2026/08/absent.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Save_EmptyKey_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.SaveAsync("", Utf8Stream("x"), "text/plain", CancellationToken.None));
    }

    // --- Path traversal: storage keys are opaque and must never escape the root (design.md §10) ---

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("attachments/../../outside.txt")]
    [InlineData("attachments/2026/../../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("attachments\\2026\\08\\file.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\config")]
    public async Task Save_TraversalOrRootedKey_ThrowsAndNeverEscapesRoot(string maliciousKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.SaveAsync(maliciousKey, Utf8Stream("payload"), "text/plain", CancellationToken.None));

        // Belt and suspenders: prove nothing was actually written outside the root,
        // in case a future change weakens the check but happens to not throw.
        var outsideCandidate = Path.Combine(Path.GetTempPath(), "outside.txt");
        Assert.False(File.Exists(outsideCandidate));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("attachments/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/etc/passwd")]
    public async Task OpenRead_TraversalOrRootedKey_Throws(string maliciousKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.OpenReadAsync(maliciousKey, CancellationToken.None));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task Delete_TraversalKey_Throws(string maliciousKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.DeleteAsync(maliciousKey, CancellationToken.None));
    }

    [Fact]
    public void Constructor_MissingRoot_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FileSystemFileStorage(Options.Create(new FileStorageOptions())));
    }
}
