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

    /// <summary>
    /// Payload signing may only be skipped over TLS — the AWS SDK throws
    /// "When DisablePayloadSigning is true, the request must be sent over HTTPS"
    /// otherwise. This was hard-coded to always-skip, so the first upload this provider
    /// ever attempted against a real endpoint (MinIO over http://) returned a 500. The
    /// http:// case is the one that regressed and the one most deployments hit: an
    /// in-network S3 store behind design.md §9.4's boundary is commonly plain HTTP.
    /// </summary>
    [Theory]
    // No ServiceUrl = real AWS via default endpoint resolution, which is HTTPS.
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://s3.example.internal", true)]
    [InlineData("https://minio:9000", true)]
    [InlineData("HTTPS://MINIO:9000", true)]
    [InlineData("http://minio:9000", false)]
    [InlineData("http://localhost:9000", false)]
    // Unparseable: assume the unsafe case rather than emitting a request that throws.
    [InlineData("minio:9000", false)]
    public void PayloadSigningIsSkippedOnlyOverTls(string? serviceUrl, bool expected)
    {
        Assert.Equal(expected, S3FileStorage.CanDisablePayloadSigning(serviceUrl));
    }
}
