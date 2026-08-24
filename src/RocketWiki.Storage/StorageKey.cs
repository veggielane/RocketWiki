namespace RocketWiki.Storage;

/// <summary>
/// Provider-independent storage-key validation, run by BOTH <see cref="IFileStorage"/>
/// implementations before any I/O — filesystem path resolution or S3 SDK call.
/// Keys are system-generated today (<c>attachments/{yyyy}/{MM}/{guid}</c> and
/// friends — design.md §10: opaque, never derived from titles, file names, or
/// anything else a user types), so every rejection here is defense-in-depth
/// against a future bug routing user-influenced text into a key, not expected
/// traffic. What it refuses, and why per provider:
/// <list type="bullet">
/// <item>null/empty/whitespace — no object legitimately lives at "nothing";</item>
/// <item><c>\</c> — keys are forward-slash-separated everywhere; on the
/// filesystem a backslash is a Windows separator, and in S3 it silently names a
/// different object than the <c>Attachment</c> row records;</item>
/// <item>leading <c>/</c> — a rooted path on the filesystem; in S3 an empty
/// leading segment, so the object is unreachable by its recorded key;</item>
/// <item><c>.</c>/<c>..</c> segments — path traversal on the filesystem; S3
/// stores them literally, but any normalizing layer in front of a bucket
/// (console, gateway, mounted filesystem) would resolve them to a different
/// object than the row names.</item>
/// </list>
/// <see cref="FileSystemFileStorage"/> keeps its own rooted-path and
/// resolved-path-stays-under-root checks on top — the containment proof only
/// that provider can make.
/// </summary>
internal static class StorageKey
{
    public static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Storage key must not be null or empty.", nameof(key));
        }

        if (key.Contains('\\'))
        {
            throw new ArgumentException(
                $"Storage key '{key}' must be a relative, forward-slash-separated path.", nameof(key));
        }

        if (key.StartsWith('/'))
        {
            throw new ArgumentException(
                $"Storage key '{key}' must not start with '/'.", nameof(key));
        }

        foreach (var segment in key.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException(
                    $"Storage key '{key}' must not contain '.' or '..' segments.", nameof(key));
            }
        }
    }
}
