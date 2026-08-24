using Microsoft.Extensions.Options;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// S3FileStorage's key-rejection paths (design.md §10). The provider must refuse a
/// malformed key BEFORE any SDK call — so no live S3 endpoint is needed here, and
/// the storage is deliberately constructed with a null IAmazonS3: if validation
/// ever slipped to after the first client touch, these tests would fail with a
/// NullReferenceException instead of the ArgumentException they assert. The
/// provider's live object-store behavior (put/get/delete/metadata against a real
/// bucket) remains uncovered by this project, as FileSystemFileStorageTests' doc
/// already states.
/// </summary>
public sealed class S3FileStorageTests
{
    private static S3FileStorage CreateStorage() => new(
        client: null!,
        Options.Create(new FileStorageOptions
        {
            S3 = new S3FileStorageOptions { Bucket = "test-bucket" },
        }));

    private static Stream Payload() => new MemoryStream([1, 2, 3]);

    public static TheoryData<string> MaliciousKeys() => new(
        "",
        "   ",
        "attachments\\2026\\08\\file.bin",
        "..\\outside.txt",
        "C:\\Windows\\System32\\config",
        "/etc/passwd",
        "../outside.txt",
        "attachments/../../outside.txt",
        "attachments/./file.bin");

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task Save_MalformedKey_ThrowsBeforeAnySdkCall(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().SaveAsync(key, Payload(), "application/octet-stream", CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task OpenRead_MalformedKey_ThrowsBeforeAnySdkCall(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().OpenReadAsync(key, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task Delete_MalformedKey_ThrowsBeforeAnySdkCall(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().DeleteAsync(key, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task Exists_MalformedKey_ThrowsBeforeAnySdkCall(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public void Constructor_MissingBucket_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new S3FileStorage(client: null!, Options.Create(new FileStorageOptions())));
    }
}
