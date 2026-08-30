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

    /// <summary>
    /// The positive control for every rejection theory above, the same one
    /// SqlServerFileStorageTests added deliberately. Without it those theories prove
    /// nothing about WHERE validation happens: a provider that threw ArgumentException
    /// for every key, or whose method body had been deleted entirely, would satisfy
    /// them all. A well-formed key must get PAST validation and reach the SDK — here
    /// a deliberately null client, so "reached the client" shows up as
    /// NullReferenceException rather than ArgumentException.
    /// </summary>
    [Fact]
    public async Task WellFormedKey_ReachesTheSdk_SoTheRejectionsAboveAreNotVacuous()
    {
        var thrown = await Record.ExceptionAsync(() => CreateStorage().SaveAsync(
            "attachments/2026/08/well-formed.bin", Payload(), "application/octet-stream", CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.IsNotType<ArgumentException>(thrown);
    }
    /// <summary>
    /// The GetObjectResponse owns the stream and is itself IDisposable; returning the
    /// bare ResponseStream dropped it, so the caller could not dispose what it never
    /// received. SqlServerFileStorage built an explicit wrapper for the identical
    /// ownership problem, with a MaxPoolSize=2 regression test, because leaking the
    /// handle there exhausts the pool after a few hundred downloads. This asserts the
    /// S3 provider now returns a stream that owns its response too.
    /// </summary>
    [Fact]
    public async Task OpenRead_ReturnsAStreamThatDisposesTheOwningResponse()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var tracked = new TrackingStream(payload);
        var response = new Amazon.S3.Model.GetObjectResponse { ResponseStream = tracked };
        var storage = new S3FileStorage(
            new StubS3Client(response),
            Options.Create(new FileStorageOptions { S3 = new S3FileStorageOptions { Bucket = "test-bucket" } }));

        var stream = await storage.OpenReadAsync("attachments/2026/08/object.bin", CancellationToken.None);

        // Non-vacuous: the bytes really do come through the wrapper.
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }

        // The discriminating assertion: the caller must NOT be handed the response’s
        // own stream. Handing that back is precisely what dropped the owning response,
        // and "the inner stream got disposed" is true either way, so it proves nothing.
        Assert.NotSame(tracked, stream);

        Assert.False(tracked.Disposed);
        await stream.DisposeAsync();
        Assert.True(tracked.Disposed,
            "Disposing the returned wrapper must dispose the response's stream too.");
    }

    /// <summary>Observes disposal through the stream the response hands out: the SDK's
    /// response type seals its own disposal, so the tracking sits one level in.</summary>
    private sealed class TrackingStream(byte[] payload) : MemoryStream(payload)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Answers exactly one GetObject; every other member is unreachable here.</summary>
    private sealed class StubS3Client(Amazon.S3.Model.GetObjectResponse response) : Amazon.S3.AmazonS3Client("id", "secret", Amazon.RegionEndpoint.USEast1)
    {
        public override Task<Amazon.S3.Model.GetObjectResponse> GetObjectAsync(
            string bucketName, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);
    }
}
