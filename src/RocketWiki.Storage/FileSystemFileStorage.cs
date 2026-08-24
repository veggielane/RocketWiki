using Microsoft.Extensions.Options;

namespace RocketWiki.Storage;

/// <summary>
/// Directory-backed <see cref="IFileStorage"/> for local dev, integration tests,
/// and the simplest single-box deployments (design.md §10).
/// </summary>
public sealed class FileSystemFileStorage : IFileStorage
{
    private readonly string _root;

    public FileSystemFileStorage(IOptions<FileStorageOptions> options)
    {
        var configuredRoot = options.Value.FileSystem?.Root;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                "FileStorage:FileSystem:Root must be configured when using the FileSystem provider.");
        }

        // Resolved once, up front, so every later containment check compares
        // against a single canonical, fully-qualified root.
        _root = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.FileSystemProvider, StorageTelemetry.SaveOperation);
        try
        {
            var path = ResolvePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // FileMode.Create: upload order is "bytes to storage, then the Attachment
            // row + audit event in one DB transaction" (design.md §10) — a re-upload
            // under the same key is expected to overwrite, not append or fail.
            await using var fileStream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            await content.CopyToAsync(fileStream, ct);

            // Measured from what was actually written, rather than the source stream's
            // Length, which a non-seekable upload stream doesn't have.
            operation.Bytes = fileStream.Length;

            // contentType is deliberately unused here: this provider has no companion
            // metadata store, and Attachment.ContentType (data-model.md) is the
            // source of truth the API serves back to clients. Kept as a parameter to
            // satisfy IFileStorage and to keep provider parity with S3FileStorage,
            // which does need it (S3 objects carry their own Content-Type).
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.FileSystemProvider, StorageTelemetry.OpenReadOperation);
        try
        {
            var path = ResolvePath(key);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"No object exists for key '{key}'.", path);
            }

            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            operation.Bytes = stream.Length;
            return Task.FromResult<Stream>(stream);
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.FileSystemProvider, StorageTelemetry.DeleteOperation);
        try
        {
            var path = ResolvePath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else
            {
                operation.SetOutcome("not_found");
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        using var operation = StorageTelemetry.StartOperation(StorageTelemetry.FileSystemProvider, StorageTelemetry.ExistsOperation);
        try
        {
            var path = ResolvePath(key);
            var exists = File.Exists(path);
            operation.SetOutcome(exists ? "found" : "not_found");
            return Task.FromResult(exists);
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Maps an opaque storage key onto a path under <see cref="_root"/>, refusing
    /// anything that could escape it. Storage keys are never attacker-controlled
    /// path fragments by design (design.md §10), but this provider fails closed
    /// on a bad key rather than trusting that invariant blindly.
    /// </summary>
    private string ResolvePath(string key)
    {
        // Provider-independent rejections shared with S3FileStorage; see StorageKey.
        StorageKey.Validate(key);

        // On top of the shared checks: rooted forms only a filesystem sees — on
        // Windows a rooted path can also start with a drive letter or UNC prefix
        // (with forward slashes, so the shared backslash check can't catch it),
        // none of which a legitimate key ever contains.
        if (Path.IsPathRooted(key))
        {
            throw new ArgumentException(
                $"Storage key '{key}' must be a relative, forward-slash-separated path.", nameof(key));
        }

        var combined = Path.GetFullPath(Path.Combine(_root, key));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        // The real defense: however the key is spelled, the resolved path must
        // still land inside the root. Catches ".." segments, encoded traversal,
        // and anything else the checks above didn't anticipate.
        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Storage key '{key}' resolves outside the configured storage root.", nameof(key));
        }

        return combined;
    }
}
