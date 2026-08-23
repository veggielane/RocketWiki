using System.Text;
using RocketWiki.Api.Emojis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// Direct unit coverage of the decode-guard/normalize/re-encode pipeline — the pure
/// logic tier (design.md §14) for the parts of the emoji feature that never need HTTP:
/// the format allowlist, the header-stage refusals, and the squaring rules. The HTTP
/// behavior built on top lives in Integration/CustomEmojiEndpointTests.
/// </summary>
public class EmojiImageProcessorTests
{
    private const long GenerousCap = 10 * 1024 * 1024;

    private static byte[] Encode(int width, int height, IImageEncoder encoder)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(120, 40, 200, 255));
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    [Fact]
    public void ArbitraryBytes_AreRefused()
    {
        var outcome = EmojiImageProcessor.Process(Encoding.UTF8.GetBytes("<svg>not an image</svg>"), GenerousCap);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("PNG, JPEG, WebP, or GIF", outcome.FailureReason);
    }

    [Fact]
    public void DisallowedButValidFormat_Bmp_IsRefused_ByTheAllowlistItself()
    {
        var outcome = EmojiImageProcessor.Process(Encode(64, 64, new BmpEncoder()), GenerousCap);

        Assert.False(outcome.IsSuccess);
    }

    [Fact]
    public void Webp_IsAccepted_AndNormalizedToPng()
    {
        var outcome = EmojiImageProcessor.Process(Encode(64, 64, new WebpEncoder()), GenerousCap);

        Assert.True(outcome.IsSuccess, outcome.FailureReason);
        Assert.Equal("image/png", outcome.Result!.ContentType);
        Assert.Equal(64, outcome.Result.PixelSize);
    }

    [Theory]
    [InlineData(16, 16)] // under the 32px floor
    [InlineData(31, 31)] // one short of it
    public void TooSmall_IsRefused_NeverUpscaled(int width, int height)
    {
        var outcome = EmojiImageProcessor.Process(Encode(width, height, new PngEncoder()), GenerousCap);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("never upscaled", outcome.FailureReason);
    }

    [Fact]
    public void AspectRatioBeyondTwoToOne_IsRefused()
    {
        var outcome = EmojiImageProcessor.Process(Encode(129, 64, new PngEncoder()), GenerousCap);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("aspect ratio", outcome.FailureReason);
    }

    [Fact]
    public void ExactlyTwoToOne_IsPaddedToSquare()
    {
        var outcome = EmojiImageProcessor.Process(Encode(128, 64, new PngEncoder()), GenerousCap);

        Assert.True(outcome.IsSuccess, outcome.FailureReason);
        Assert.Equal(128, outcome.Result!.PixelSize);
    }

    [Fact]
    public void LargeSquare_IsDownscaledTo256()
    {
        var outcome = EmojiImageProcessor.Process(Encode(1000, 1000, new PngEncoder()), GenerousCap);

        Assert.True(outcome.IsSuccess, outcome.FailureReason);
        Assert.Equal(256, outcome.Result!.PixelSize);
        using var image = Image.Load<Rgba32>(outcome.Result.Bytes);
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
    }

    [Fact]
    public void SourceBeyondDecodeDimensionLimit_IsRefusedFromTheHeader()
    {
        var outcome = EmojiImageProcessor.Process(Encode(5000, 2500, new PngEncoder()), GenerousCap);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("decode limit", outcome.FailureReason);
    }

    [Fact]
    public void ReencodedOutputOverTheByteCap_IsRefused()
    {
        // A generous decode but a 1-byte storage cap: the post-encode check must fire.
        var outcome = EmojiImageProcessor.Process(Encode(64, 64, new PngEncoder()), maxEncodedBytes: 1);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("byte", outcome.FailureReason);
    }

    [Fact]
    public void AnimatedGif_KeepsFrames_AsGif()
    {
        using var image = new Image<Rgba32>(64, 64, new Rgba32(255, 0, 0, 255));
        using var second = new Image<Rgba32>(64, 64, new Rgba32(0, 255, 0, 255));
        image.Frames.AddFrame(second.Frames.RootFrame);
        using var stream = new MemoryStream();
        image.SaveAsGif(stream);

        var outcome = EmojiImageProcessor.Process(stream.ToArray(), GenerousCap);

        Assert.True(outcome.IsSuccess, outcome.FailureReason);
        Assert.Equal("image/gif", outcome.Result!.ContentType);
        using var reloaded = Image.Load<Rgba32>(outcome.Result.Bytes);
        Assert.Equal(2, reloaded.Frames.Count);
    }

    [Fact]
    public void AnimatedPng_IsFlattenedToOneFrame_OneAnimatedFormatOnly()
    {
        using var image = new Image<Rgba32>(64, 64, new Rgba32(255, 0, 0, 255));
        using var second = new Image<Rgba32>(64, 64, new Rgba32(0, 255, 0, 255));
        image.Frames.AddFrame(second.Frames.RootFrame);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream); // APNG

        var outcome = EmojiImageProcessor.Process(stream.ToArray(), GenerousCap);

        Assert.True(outcome.IsSuccess, outcome.FailureReason);
        Assert.Equal("image/png", outcome.Result!.ContentType);
        using var reloaded = Image.Load<Rgba32>(outcome.Result.Bytes);
        Assert.Equal(1, reloaded.Frames.Count);
    }
}
