namespace RocketWiki.Storage;

/// <summary>
/// Abstraction over attachment blob storage (design.md §10). Two providers ship
/// behind this interface: <see cref="FileSystemFileStorage"/> for dev, tests, and
/// simple single-box installs, and <see cref="S3FileStorage"/> for any
/// S3-compatible object store (MinIO, Ceph, R2, AWS S3) in production.
///
/// Storage keys are opaque strings (e.g. <c>attachments/{yyyy}/{MM}/{guid}</c>) —
/// callers never derive them from page titles, file names, or anything else that
/// can change; the <c>Attachment</c> row owns all meaning, so renames and moves
/// never touch storage (design.md §10).
///
/// Deliberately absent: any presigned-URL / direct-link method. Downloads always
/// stream through the API, because a presigned URL would bypass both page
/// restrictions (`canView`) and the audit log (design.md §7, §10). Do not add one.
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);
}
