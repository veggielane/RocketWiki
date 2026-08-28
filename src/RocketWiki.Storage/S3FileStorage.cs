using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace RocketWiki.Storage;

/// <summary>
/// S3-compatible <see cref="IFileStorage"/> provider (design.md §10). Talks to
/// any endpoint that speaks the S3 API — MinIO, Ceph, R2, or real AWS S3 — via
/// the configured <c>ServiceUrl</c> and <c>ForcePathStyle</c>, never hard-coded
/// to AWS.
///
/// No presigned-URL method exists on this class, deliberately: bytes always flow
/// through the API's own attachment routes, which enforce `canView` and audit
/// the read before streaming (design.md §7, §10). Do not add one as a
/// "convenience" — it would silently bypass both.
///
/// Every method validates its key via <see cref="StorageKey"/> before the SDK
/// is touched — the same provider-independent rejections FileSystemFileStorage
/// applies, minus the under-root containment only a filesystem can prove.
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly bool _disablePayloadSigning;

    public S3FileStorage(IAmazonS3 client, IOptions<FileStorageOptions> options)
    {
        _client = client;
        _bucket = options.Value.S3?.Bucket
            ?? throw new InvalidOperationException(
                "FileStorage:S3:Bucket must be configured when using the S3 provider.");
        _disablePayloadSigning = CanDisablePayloadSigning(options.Value.S3?.ServiceUrl);
    }

    /// <summary>
    /// Whether uploads may skip SigV4 payload signing — true only over TLS.
    ///
    /// <para>Unsigned payloads let a non-seekable stream upload without buffering the
    /// whole object to hash it, which is why this provider asked for them. But the AWS
    /// SDK refuses the combination over plain HTTP ("When DisablePayloadSigning is true,
    /// the request must be sent over HTTPS") — without TLS there is nothing else binding
    /// the body to the signature. Hard-coding it to true therefore made every upload
    /// throw a 500 against any http:// endpoint, which is how MinIO and most in-network
    /// S3-compatible stores are actually deployed (design.md §9.4 puts the boundary at
    /// the network, not at TLS). Found the first time this provider ever ran against a
    /// real endpoint rather than a wiring test.</para>
    ///
    /// <para>A null/empty ServiceUrl means real AWS via default endpoint resolution,
    /// which is HTTPS.</para>
    /// </summary>
    internal static bool CanDisablePayloadSigning(string? serviceUrl) =>
        string.IsNullOrEmpty(serviceUrl)
        || (Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.S3Provider, StorageTelemetry.SaveOperation);
        try
        {
            StorageKey.Validate(key);
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
                DisablePayloadSigning = _disablePayloadSigning,
            };

            // Only a seekable stream can report its size without consuming it; a
            // non-seekable upload simply records no byte count rather than buffering
            // the whole attachment in memory to measure it.
            if (content.CanSeek)
            {
                operation.Bytes = content.Length;
            }

            await _client.PutObjectAsync(request, ct);
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.S3Provider, StorageTelemetry.OpenReadOperation);
        try
        {
            StorageKey.Validate(key);
            var response = await _client.GetObjectAsync(_bucket, key, ct);
            operation.Bytes = response.ContentLength;
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            operation.SetOutcome("not_found");
            throw new FileNotFoundException($"No object exists for key '{key}'.", ex);
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.S3Provider, StorageTelemetry.DeleteOperation);
        try
        {
            StorageKey.Validate(key);
            await _client.DeleteObjectAsync(_bucket, key, ct);
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.S3Provider, StorageTelemetry.ExistsOperation);
        try
        {
            StorageKey.Validate(key);
            await _client.GetObjectMetadataAsync(_bucket, key, cancellationToken: ct);
            operation.SetOutcome("found");
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            operation.SetOutcome("not_found");
            return false;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }
}
