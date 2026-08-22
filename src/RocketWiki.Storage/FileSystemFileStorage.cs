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
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // FileMode.Create: upload order is "bytes to storage, then the Attachment
        // row + audit event in one DB transaction" (design.md §10) — a re-upload
        // under the same key is expected to overwrite, not append or fail.
        await using var fileStream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(fileStream, ct);

        // contentType is deliberately unused here: this provider has no companion
        // metadata store, and Attachment.ContentType (data-model.md) is the
        // source of truth the API serves back to clients. Kept as a parameter to
        // satisfy IFileStorage and to keep provider parity with S3FileStorage,
        // which does need it (S3 objects carry their own Content-Type).
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No object exists for key '{key}'.", path);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        return Task.FromResult(File.Exists(path));
    }

    /// <summary>
    /// Maps an opaque storage key onto a path under <see cref="_root"/>, refusing
    /// anything that could escape it. Storage keys are never attacker-controlled
    /// path fragments by design (design.md §10), but this provider fails closed
    /// on a bad key rather than trusting that invariant blindly.
    /// </summary>
    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Storage key must not be null or empty.", nameof(key));
        }

        // Keys are forward-slash-separated (attachments/{yyyy}/{MM}/{guid}).
        // Reject backslashes and absolute/rooted paths outright — on Windows a
        // rooted path can also start with a drive letter or UNC prefix, none of
        // which a legitimate key ever contains.
        if (key.Contains('\\') || Path.IsPathRooted(key))
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
