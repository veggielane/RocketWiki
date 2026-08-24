using System.ComponentModel.DataAnnotations;

namespace RocketWiki.Api.Emojis;

/// <summary>
/// Custom-emoji upload policy, bound from the <c>Emojis</c> configuration section
/// (Program.cs, beside the attachment and avatar caps it is a sibling of) and validated
/// at startup — a misconfigured cap fails the host rather than the first upload.
/// </summary>
public sealed class EmojiOptions
{
    public const string SectionName = "Emojis";

    /// <summary>
    /// 256 KiB. Applies twice, deliberately: to the raw upload (refused with a
    /// structured 413 before any decode — the transport bound) and to the re-encoded
    /// bytes that would actually be stored (refused as a validation error — e.g. an
    /// animated GIF that only fits the cap while over-dimensioned). An emoji renders
    /// at text height; a quarter-megabyte ceiling is generous for a 256 px square.
    /// </summary>
    public const long DefaultMaxSizeBytes = 256 * 1024;

    /// <summary>Maximum accepted emoji upload in bytes. Must be positive: a zero or
    /// negative cap would refuse every upload with a 413, which is a configuration
    /// mistake worth failing startup over rather than a policy anyone means.</summary>
    [Range(1, long.MaxValue, ErrorMessage = "Emojis:MaxSizeBytes must be a positive number of bytes.")]
    public long MaxSizeBytes { get; set; } = DefaultMaxSizeBytes;
}
