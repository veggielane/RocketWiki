using RocketWiki.Core.Content;
using RocketWiki.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RocketWiki.Api.Avatars;

/// <summary>
/// <see cref="IAvatarImageProcessor"/> over SixLabors.ImageSharp (pinned 4.1.1 —
/// license note in the csproj). Upload bytes are untrusted input, so decoding runs
/// against a private <see cref="Configuration"/> that is limited three ways before
/// any pixel work happens:
///
/// <list type="bullet">
/// <item><b>Format allow-list.</b> Only the PNG, JPEG, and WebP decoders exist in
/// this configuration — every other format (SVG has no decoder in ImageSharp at all,
/// GIF/TIFF/BMP/TGA/QOI/PBM deliberately absent) fails identification, whatever the
/// file claims to be.</item>
/// <item><b>Dimension gate before decode.</b> <c>Image.Identify</c> reads only the
/// header; anything declaring more than <see cref="MaxSourceDimension"/> on either
/// side is refused before a pixel buffer is allocated, so a decompression-bomb
/// header can't request gigabytes.</item>
/// <item><b>Memory ceiling as backstop.</b> The configuration's allocator caps any
/// single decode at <see cref="AllocationLimitMegabytes"/> — if a malformed file
/// gets past the header gate, allocation fails (a catchable
/// <see cref="InvalidMemoryOperationException"/>), never an unbounded buffer.
/// <c>MaxFrames = 1</c> bounds animated WebP the same way; <c>SkipMetadata</c> means
/// EXIF/XMP/ICC are never even parsed, let alone re-emitted.</item>
/// </list>
///
/// Normalization is decode → center-crop to square + resize to
/// <see cref="PngHeader.AvatarDimension"/> (one <c>ResizeMode.Crop</c> pass) →
/// re-encode to PNG. Only the re-encoded output is ever returned; metadata profiles
/// are additionally nulled before encode (belt and braces over SkipMetadata), and
/// <see cref="PngHeader.IsAvatarPng"/> — the hand-rolled header check, kept exactly
/// for this — verifies the output invariant independently of the library, so a
/// future package upgrade that changed encoding behavior would fail loudly here
/// rather than store a non-canonical object.
/// </summary>
public sealed class ImageSharpAvatarProcessor : IAvatarImageProcessor
{
    /// <summary>Per-side cap on source dimensions. 8192² covers every current phone
    /// camera output while capping the decoded RGBA buffer at ~256 MB.</summary>
    internal const int MaxSourceDimension = 8192;

    private const int AllocationLimitMegabytes = 320;

    private static readonly Configuration ProcessorConfiguration = CreateConfiguration();

    private static Configuration CreateConfiguration()
    {
        var configuration = new Configuration(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new WebpConfigurationModule())
        {
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                AllocationLimitMegabytes = AllocationLimitMegabytes,
            }),
        };
        return configuration;
    }

    private static DecoderOptions CreateDecoderOptions() => new()
    {
        Configuration = ProcessorConfiguration,
        MaxFrames = 1,
        SkipMetadata = true,
    };

    public AvatarImageResult Normalize(byte[] upload)
    {
        try
        {
            var options = CreateDecoderOptions();

            // Header-only pass: refuses non-allow-listed formats and oversized
            // declarations before any pixel allocation.
            var info = Image.Identify(options, upload);
            if (info.Width > MaxSourceDimension || info.Height > MaxSourceDimension)
            {
                return new AvatarImageResult.Invalid(
                    $"Avatar image dimensions must not exceed {MaxSourceDimension}x{MaxSourceDimension}.");
            }

            using var image = Image.Load<Rgba32>(options, upload);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(PngHeader.AvatarDimension, PngHeader.AvatarDimension),
                Mode = ResizeMode.Crop,               // center-crop to square + scale, one pass
                Position = AnchorPositionMode.Center,
            }));

            // SkipMetadata means these should already be empty; nulling them anyway
            // makes "no source metadata survives" a property of this code, not of a
            // library default.
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.CicpProfile = null;

            using var output = new MemoryStream();
            image.SaveAsPng(output, new PngEncoder());
            var png = output.ToArray();

            if (!PngHeader.IsAvatarPng(png))
            {
                // Library-independent output invariant - unreachable unless an
                // ImageSharp upgrade changes encoding in a way this feature must not
                // silently absorb.
                throw new InvalidOperationException(
                    "Avatar normalization produced bytes that are not a canonical " +
                    $"{PngHeader.AvatarDimension}x{PngHeader.AvatarDimension} PNG.");
            }

            return new AvatarImageResult.Ok(png);
        }
        catch (Exception e) when (e is ImageFormatException or InvalidMemoryOperationException)
        {
            // UnknownImageFormatException / InvalidImageContentException both derive
            // from ImageFormatException; the allocator ceiling surfaces as
            // InvalidMemoryOperationException. All are one refusal category, and the
            // message deliberately describes the contract, not the bytes.
            return new AvatarImageResult.Invalid(
                "Avatar must be a valid PNG, JPEG, or WebP image within the size limits.");
        }
    }

    public byte[] ResizeCanonicalPng(byte[] canonicalPng, int size)
    {
        if (size <= 0 || size >= PngHeader.AvatarDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size,
                $"Resize targets must be within (0, {PngHeader.AvatarDimension}) - serve the stored bytes for the canonical size.");
        }

        // Trusted input (this system produced and invariant-checked it), but decoded
        // under the same limited configuration anyway - one code path, no exceptions.
        using var image = Image.Load<Rgba32>(CreateDecoderOptions(), canonicalPng);
        image.Mutate(x => x.Resize(size, size));

        using var output = new MemoryStream();
        image.SaveAsPng(output, new PngEncoder());
        return output.ToArray();
    }
}
