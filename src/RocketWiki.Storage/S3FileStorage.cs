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
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3FileStorage(IAmazonS3 client, IOptions<FileStorageOptions> options)
    {
        _client = client;
        _bucket = options.Value.S3?.Bucket
            ?? throw new InvalidOperationException(
                "FileStorage:S3:Bucket must be configured when using the S3 provider.");
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            DisablePayloadSigning = true,
        };

        await _client.PutObjectAsync(request, ct);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectAsync(_bucket, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"No object exists for key '{key}'.", ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await _client.DeleteObjectAsync(_bucket, key, ct);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, key, cancellationToken: ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
