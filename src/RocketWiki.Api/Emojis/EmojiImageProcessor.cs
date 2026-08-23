using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RocketWiki.Api.Emojis;

/// <summary>
/// Decodes an untrusted emoji upload under explicit limits, normalizes it to a
/// 32–256 px square, and re-encodes it — <b>only the re-encoded bytes are ever
/// stored or served</b>. Re-encoding is the security boundary, not a nicety: it
/// kills polyglot files (a valid PNG that is also valid HTML/JS survives a byte-copy
/// but not a decode/re-encode), and together with the explicit
/// <see cref="ScrubMetadata"/> pass it strips every metadata chunk (EXIF, tEXt, XMP,
/// ICC). The scrub is explicit because ImageSharp deliberately round-trips metadata
/// through decode/encode — proven by test: without it, a re-encoded PNG can be
/// byte-identical to its upload, text chunks included.
///
/// Decode guards, in order of cheapness:
/// <list type="number">
/// <item>Byte cap — enforced by the route before this class ever sees the upload.</item>
/// <item>Format allowlist — the private <see cref="Configuration"/> registers only
/// PNG, JPEG, WebP and GIF decoders, so everything else (BMP, TIFF, ICO, SVG,
/// arbitrary bytes) fails as unknown-format before any pixel work. SVG stays out
/// permanently: it is a script container, not an image.</item>
/// <item>Header-only <c>Image.Identify</c> — source dimensions, aspect ratio, and
/// frame count are refused from the header alone, before full-pixel decode.</item>
/// <item>Full decode under a hard allocator budget and a frame cap, so a lying
/// header (decompression bomb) hits the allocator limit instead of the process.</item>
/// </list>
///
/// Normalization: pad to square (transparent, centered — an emoji cell is square, and
/// padding preserves the author's pixels where stretching would distort them), then
/// downscale to at most <see cref="MaxPixelSize"/>. Aspect ratios beyond
/// <see cref="MaxAspectRatio"/> are refused rather than padded: a 10:1 banner padded
/// into a square renders as an illegible stripe at text height, and refusing loudly
/// beats accepting something that can only ever look broken. Never upscaled — a
/// source smaller than <see cref="MinPixelSize"/> is refused, not lied about.
///
/// Animated GIFs are in scope: the resize is frame-preserving, frames are capped, and
/// the output re-encodes as GIF. Animated PNG/WebP inputs are flattened to their
/// first frame — one animated format is a feature, three is a support matrix.
/// </summary>
public static class EmojiImageProcessor
{
    public const int MinPixelSize = 32;
    public const int MaxPixelSize = 256;

    /// <summary>Refuse absurd source dimensions from the header, before decode.
    /// 4096² RGBA ≈ 64 MB decoded — comfortably inside the allocator budget.</summary>
    public const int MaxSourceDimension = 4096;

    public const int MaxFrames = 64;
    public const double MaxAspectRatio = 2.0;

    private const int AllocationLimitMegabytes = 128;

    /// <summary>Only these four decoders exist in this configuration — the allowlist
    /// is structural, not a check that could be forgotten.</summary>
    private static readonly Configuration RestrictedConfiguration = CreateRestrictedConfiguration();

    private static Configuration CreateRestrictedConfiguration()
    {
        var configuration = new Configuration(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new WebpConfigurationModule(),
            new GifConfigurationModule())
        {
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                AllocationLimitMegabytes = AllocationLimitMegabytes,
            }),
        };
        return configuration;
    }

    public sealed record EmojiImageResult(byte[] Bytes, string ContentType, int PixelSize);

    /// <summary>Failure reasons are structured client feedback (a 400 body for the
    /// uploading admin) — they never enter audit details or telemetry.</summary>
    public sealed record EmojiImageOutcome(EmojiImageResult? Result, string? FailureReason)
    {
        public bool IsSuccess => Result is not null;

        internal static EmojiImageOutcome Success(EmojiImageResult result) => new(result, null);

        internal static EmojiImageOutcome Failure(string reason) => new(null, reason);
    }

    public static EmojiImageOutcome Process(byte[] source, long maxEncodedBytes)
    {
        // MaxFrames + 1: the decoder silently STOPS at its cap, so decoding at exactly
        // MaxFrames could not tell "legitimately 64 frames" from "truncated 400-frame
        // bomb". One frame of headroom makes over-cap detectable (Count > MaxFrames)
        // and refusable, while still bounding what a hostile file can make us decode.
        var decoderOptions = new DecoderOptions
        {
            Configuration = RestrictedConfiguration,
            MaxFrames = MaxFrames + 1,
        };

        // Header-only pre-check: refuse oversized/misshapen sources before spending a
        // full-pixel decode on them - an optimization and an early, precise error,
        // never the only guard (the decode below is independently bounded).
        try
        {
            var info = Image.Identify(decoderOptions, source);
            if (DimensionRefusal(info.Width, info.Height) is { } headerRefusal)
            {
                return headerRefusal;
            }
        }
        catch (UnknownImageFormatException)
        {
            return EmojiImageOutcome.Failure("Unsupported image format. Emojis must be PNG, JPEG, WebP, or GIF (never SVG).");
        }
        catch (InvalidImageContentException)
        {
            // Upstream quirk, verified against 3.1.12: PNG's metadata-only Identify
            // throws "Invalid PNG data." on APNG streams the full decoder accepts
            // (including ones ImageSharp's own encoder produced). Fall through to the
            // full decode rather than refusing a legitimate upload: the allocator
            // budget + frame cap still bound a hostile file, and the same dimension
            // checks rerun on the decoded image below.
        }

        try
        {
            using var image = Image.Load<Rgba32>(decoderOptions, source);

            if (DimensionRefusal(image.Width, image.Height) is { } refusal)
            {
                return refusal;
            }

            var isGif = image.Metadata.DecodedImageFormat is GifFormat;

            if (isGif && image.Frames.Count > MaxFrames)
            {
                return EmojiImageOutcome.Failure($"Animated emojis are limited to {MaxFrames} frames.");
            }

            // Animated PNG/WebP: flatten to the first frame (see class doc).
            if (!isGif)
            {
                while (image.Frames.Count > 1)
                {
                    image.Frames.RemoveFrame(image.Frames.Count - 1);
                }
            }

            ScrubMetadata(image, isGif);

            // One operation does both normalization steps, across every frame:
            // ResizeMode.Pad scales to fit the square box preserving aspect ratio
            // (never upscaling past the source, since target <= longSide), then
            // center-pads the remainder with transparency.
            var targetSize = Math.Min(Math.Max(image.Width, image.Height), MaxPixelSize);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(targetSize, targetSize),
                Mode = ResizeMode.Pad,
                PadColor = Color.Transparent,
            }));

            using var output = new MemoryStream();
            if (isGif)
            {
                image.SaveAsGif(output);
            }
            else
            {
                image.SaveAsPng(output);
            }

            if (output.Length > maxEncodedBytes)
            {
                return EmojiImageOutcome.Failure(
                    $"The normalized image is {output.Length} bytes, over the {maxEncodedBytes}-byte limit. Reduce dimensions or frame count.");
            }

            return EmojiImageOutcome.Success(new EmojiImageResult(
                output.ToArray(), isGif ? "image/gif" : "image/png", targetSize));
        }
        catch (UnknownImageFormatException)
        {
            return EmojiImageOutcome.Failure("Unsupported image format. Emojis must be PNG, JPEG, WebP, or GIF (never SVG).");
        }
        catch (InvalidImageContentException)
        {
            return EmojiImageOutcome.Failure("The image could not be decoded - the file appears corrupt or truncated.");
        }
        catch (InvalidMemoryOperationException)
        {
            // The allocator budget tripped: a small file declaring/producing enormous
            // pixel data (decompression bomb). Same refusal shape as any other
            // validation failure - the guard exists precisely so this is a 400, not
            // an OOM.
            return EmojiImageOutcome.Failure("The image expands beyond the decoder's memory budget and was refused.");
        }
    }

    private static EmojiImageOutcome? DimensionRefusal(int width, int height)
    {
        if (width > MaxSourceDimension || height > MaxSourceDimension)
        {
            return EmojiImageOutcome.Failure(
                $"Image dimensions {width}x{height} exceed the {MaxSourceDimension}px decode limit.");
        }

        var longSide = Math.Max(width, height);
        var shortSide = Math.Min(width, height);
        if (longSide < MinPixelSize)
        {
            return EmojiImageOutcome.Failure(
                $"Image is {width}x{height}; the longer side must be at least {MinPixelSize}px (emojis are never upscaled).");
        }

        if (shortSide == 0 || (double)longSide / shortSide > MaxAspectRatio)
        {
            return EmojiImageOutcome.Failure(
                $"Image aspect ratio {width}:{height} is too extreme; at most {MaxAspectRatio}:1 can be padded to a legible square.");
        }

        return null;
    }

    /// <summary>
    /// ImageSharp round-trips metadata through decode/encode by design, so re-encoding
    /// alone does NOT strip it (a test proved a re-encoded PNG byte-identical to its
    /// upload, tEXt chunk included). Everything identifying or free-text goes,
    /// image-level and per-frame: EXIF, XMP, ICC, IPTC profiles, PNG text chunks, GIF
    /// comments. Format-structural metadata (frame delays, bit depth) stays - it is
    /// what makes the image an image.
    /// </summary>
    private static void ScrubMetadata(Image image, bool isGif)
    {
        var metadata = image.Metadata;
        metadata.ExifProfile = null;
        metadata.XmpProfile = null;
        metadata.IccProfile = null;
        metadata.IptcProfile = null;

        if (isGif)
        {
            metadata.GetGifMetadata().Comments.Clear();
        }
        else
        {
            metadata.GetPngMetadata().TextData.Clear();
        }

        foreach (var frame in image.Frames)
        {
            frame.Metadata.ExifProfile = null;
            frame.Metadata.XmpProfile = null;
            frame.Metadata.IccProfile = null;
            frame.Metadata.IptcProfile = null;
        }
    }
}
