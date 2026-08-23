namespace RocketWiki.Api.Attachments;

/// <summary>
/// Attachment upload policy, bound from the <c>Attachments</c> configuration section
/// (Program.cs). Exists because deploy/README flagged that nginx's
/// <c>client_max_body_size 100m</c> (deploy/docker/nginx/default.conf.template) was
/// the ONLY upload cap in the system — a proxy default must not be the API's security
/// posture. This is now the one declared limit; every enforcement layer
/// (<see cref="AttachmentEndpoints"/>) derives from it, and the nginx line matches it
/// rather than defining it.
/// </summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    /// <summary>
    /// 100 MiB. design.md §10 states no number, so the default deliberately matches
    /// the nginx <c>client_max_body_size 100m</c> already deployed in front of the
    /// API (nginx sizes are 1024-based), keeping the two caps identical out of the
    /// box instead of introducing a second, subtly different one.
    /// </summary>
    public const long DefaultMaxSizeBytes = 100L * 1024 * 1024;

    /// <summary>Maximum accepted attachment size in bytes. A file of exactly this
    /// size is accepted; one byte more is refused with 413 before any blob or row is
    /// written.</summary>
    public long MaxSizeBytes { get; set; } = DefaultMaxSizeBytes;
}
