namespace RocketWiki.Storage;

/// <summary>
/// Provider-independent storage-key validation, run by EVERY <see cref="IFileStorage"/>
/// implementation before any I/O — filesystem path resolution, S3 SDK call, or opening a
/// SQL connection.
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
/// <see cref="SqlServerFileStorage"/> stores keys as primary-key values, where none of
/// the above is dangerous by itself — it applies the same rules anyway, so a key
/// rejected by one provider is rejected by all three and a deployment cannot discover
/// on migration that its keys were only ever legal on the old one.
///
/// <para><b>Validation parity is not round-trip parity, and this comment used to claim
/// it was.</b> A key these rules ACCEPT can still behave differently per provider,
/// because the filesystem is not a key-value store:
/// <list type="bullet">
/// <item><b>Case.</b> S3 and the SQL Server table (BIN2 collation) treat
/// <c>a/B</c> and <c>a/b</c> as two objects; NTFS and APFS do not.</item>
/// <item><b>Trailing separators, doubled separators, trailing dots and spaces.</b>
/// <c>a//b</c> and <c>a/b</c> are distinct objects in S3 and identical paths on
/// Windows, which also strips a trailing <c>.</c> or space; a trailing <c>/</c> is a
/// legal S3 key and a directory path on disk.</item>
/// </list>
/// None of this is reachable from RocketWiki's own keys — every one is generated
/// (<c>attachments/{yyyy}/{MM}/{guid}</c> and friends), lowercase-hex, single-slashed
/// and never user-supplied — which is why it has never bitten. It is written down
/// because the promise this paragraph replaced would be believed by whoever first
/// takes a key from somewhere else.</para>
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
