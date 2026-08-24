using System.ComponentModel.DataAnnotations;

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
///
/// Validated at startup (<c>AddFileStorage</c> wires
/// <c>ValidateDataAnnotations().ValidateOnStart()</c>). Nothing here is <i>required</i>:
/// an empty section is a supported state that selects the FileSystem provider, so
/// validation only rejects values that are present and malformed.
/// </summary>
public sealed class FileStorageOptions : IValidatableObject
{
    public const string SectionName = "FileStorage";

    /// <summary>"FileSystem" (default) or "S3". Unrecognized values fail closed
    /// at startup rather than silently falling back — AddFileStorage's switch throws
    /// while the container is still being built (which is earlier, and therefore what
    /// an operator actually sees); this annotation states the same rule declaratively
    /// and is what a future binding path that skipped that switch would still hit.
    /// Null/empty pass: unset means FileSystem.</summary>
    [RegularExpression("FileSystem|S3",
        ErrorMessage = "FileStorage:Provider must be 'FileSystem' or 'S3'; unset means FileSystem.")]
    public string? Provider { get; set; }

    public S3FileStorageOptions? S3 { get; set; }

    public FileSystemFileStorageOptions? FileSystem { get; set; }

    /// <summary>
    /// DataAnnotations validation does not recurse into complex sub-objects, so the
    /// annotations on <see cref="S3FileStorageOptions"/> would be decorative without
    /// this: the nested sections are validated explicitly, and their failures are
    /// re-reported with the configuration path an operator would actually edit
    /// (<c>FileStorage:S3:ServiceUrl</c>) rather than a bare property name.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateSection(S3, $"{SectionName}:S3"))
        {
            yield return result;
        }

        foreach (var result in ValidateSection(FileSystem, $"{SectionName}:FileSystem"))
        {
            yield return result;
        }
    }

    private static IEnumerable<ValidationResult> ValidateSection(object? section, string configurationPath)
    {
        if (section is null)
        {
            yield break;
        }

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(section, new ValidationContext(section), results, validateAllProperties: true);

        foreach (var result in results)
        {
            var keys = result.MemberNames.Select(member => $"{configurationPath}:{member}").ToArray();
            yield return new ValidationResult(
                $"{configurationPath}: {result.ErrorMessage}", keys.Length > 0 ? keys : [configurationPath]);
        }
    }
}

public sealed class S3FileStorageOptions
{
    /// <summary>Endpoint of the S3-compatible store, e.g. "http://minio:9000".
    /// Left null to use AWS's default endpoint resolution for real AWS S3. Validated
    /// at startup when set: a host:port with no scheme is the classic paste error, and
    /// it would otherwise surface as an opaque AWS SDK failure on the first upload
    /// rather than at boot.</summary>
    [Url(ErrorMessage = "ServiceUrl must be an absolute URL including its scheme, e.g. http://minio:9000.")]
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
