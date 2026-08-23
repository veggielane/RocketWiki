namespace RocketWiki.Api.Emojis;

/// <summary>
/// Custom-emoji upload policy, read from the <c>Emojis</c> configuration section.
/// Bound on demand (<see cref="FromConfiguration"/>) rather than via
/// <c>builder.Services.Configure</c> in Program.cs — a deliberate trade so the whole
/// feature's Program.cs footprint stays one self-contained endpoint-mapping block;
/// the handlers resolve only services other features already registered.
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

    public long MaxSizeBytes { get; set; } = DefaultMaxSizeBytes;

    public static EmojiOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new EmojiOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }
}
