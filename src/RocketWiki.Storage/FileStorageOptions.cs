namespace RocketWiki.Storage;

/// <summary>
/// Binds the "FileStorage" configuration section (design.md §10):
/// <code>
/// "FileStorage": {
///   "Provider": "S3",                          // or "FileSystem"
///   "S3": { "ServiceUrl": "http://minio:9000",
///           "Bucket": "rocketwiki", "ForcePathStyle": true },
///   "FileSystem": { "Root": "/data/attachments" }
/// }
/// </code>
/// </summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>"FileSystem" (default) or "S3". Unrecognized values fail closed
    /// at startup rather than silently falling back — see AddFileStorage.</summary>
    public string? Provider { get; set; }

    public S3FileStorageOptions? S3 { get; set; }

    public FileSystemFileStorageOptions? FileSystem { get; set; }
}

public sealed class S3FileStorageOptions
{
    /// <summary>Endpoint of the S3-compatible store, e.g. "http://minio:9000".
    /// Left null to use AWS's default endpoint resolution for real AWS S3.</summary>
    public string? ServiceUrl { get; set; }

    public string? Bucket { get; set; }

    /// <summary>Required for MinIO/Ceph/most non-AWS S3-compatible stores;
    /// AWS S3 itself works with either.</summary>
    public bool ForcePathStyle { get; set; } = true;

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Optional; only meaningful against real AWS S3 or providers that
    /// validate SigV4 region signing.</summary>
    public string? Region { get; set; }
}

public sealed class FileSystemFileStorageOptions
{
    /// <summary>Root directory attachments are written under. Created on first
    /// use if it doesn't exist.</summary>
    public string? Root { get; set; }
}
