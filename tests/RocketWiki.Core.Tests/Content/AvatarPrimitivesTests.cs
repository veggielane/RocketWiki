using System.Buffers.Binary;
using RocketWiki.Core.Content;
using Xunit;

namespace RocketWiki.Core.Tests.Content;

/// <summary>
/// The two pure avatar primitives (design.md §14 tier 1 — no database, no storage):
/// the PNG output-invariant parser and the Gravatar email normalization/digests.
/// </summary>
public class PngHeaderTests
{
    private static byte[] BuildPngHeader(uint width, uint height, bool corruptSignature = false, string chunkType = "IHDR")
    {
        var bytes = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        if (corruptSignature)
        {
            bytes[1] = (byte)'J';
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13); // IHDR data length
        System.Text.Encoding.ASCII.GetBytes(chunkType).CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    [Fact]
    public void ReadsDimensions_FromWellFormedHeader()
    {
        Assert.True(PngHeader.TryReadDimensions(BuildPngHeader(512, 512), out var width, out var height));
        Assert.Equal(512, width);
        Assert.Equal(512, height);
    }

    [Theory]
    [InlineData(512u, 512u, true)]
    [InlineData(512u, 256u, false)]
    [InlineData(256u, 512u, false)]
    [InlineData(1u, 1u, false)]
    public void IsAvatarPng_RequiresExactlyTheCanonicalSquare(uint width, uint height, bool expected) =>
        Assert.Equal(expected, PngHeader.IsAvatarPng(BuildPngHeader(width, height)));

    [Fact]
    public void RejectsWrongSignature()
    {
        Assert.False(PngHeader.TryReadDimensions(BuildPngHeader(512, 512, corruptSignature: true), out _, out _));
    }

    [Fact]
    public void RejectsWhenFirstChunkIsNotIhdr()
    {
        // Spec requires IHDR first; anything else in that slot is not a PNG we accept.
        Assert.False(PngHeader.TryReadDimensions(BuildPngHeader(512, 512, chunkType: "iTXt"), out _, out _));
    }

    [Fact]
    public void RejectsTruncatedAndEmptyInput()
    {
        Assert.False(PngHeader.TryReadDimensions([], out _, out _));
        Assert.False(PngHeader.TryReadDimensions(BuildPngHeader(512, 512).AsSpan(0, 20), out _, out _));
    }

    [Fact]
    public void RejectsZeroDimensions()
    {
        Assert.False(PngHeader.TryReadDimensions(BuildPngHeader(0, 512), out _, out _));
        Assert.False(PngHeader.TryReadDimensions(BuildPngHeader(512, 0), out _, out _));
    }

    [Fact]
    public void RejectsSvgBytes_AtByteZero()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""");
        Assert.False(PngHeader.TryReadDimensions(svg, out _, out _));
    }
}

public class AvatarEmailHasherTests
{
    [Fact]
    public void NormalizesTrimAndLowercase_SoEquivalentSpellingsHashIdentically()
    {
        var (md5A, sha256A) = AvatarEmailHasher.Compute("  Jane.Doe@Example.COM  ");
        var (md5B, sha256B) = AvatarEmailHasher.Compute("jane.doe@example.com");

        Assert.Equal(md5B, md5A);
        Assert.Equal(sha256B, sha256A);
    }

    [Fact]
    public void ProducesTheGravatarReferenceVectors()
    {
        // The worked example from Gravatar's own documentation.
        var (md5, sha256) = AvatarEmailHasher.Compute("MyEmailAddress@example.com");

        Assert.Equal("0bc83cb571cd1c50ba6f3e8a78ef1346", md5);
        Assert.Equal("84059b07d4be67b806386c0aad8070a23f18836bbaae342275dc0a83414c32ee", sha256);
    }

    [Fact]
    public void HashesAreLowercaseHexOfProtocolLengths()
    {
        var (md5, sha256) = AvatarEmailHasher.Compute("someone@example.test");

        Assert.NotNull(md5);
        Assert.NotNull(sha256);
        Assert.Equal(32, md5!.Length);
        Assert.Equal(64, sha256!.Length);
        Assert.All(md5 + sha256, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoEmail_MeansNoHashes_NeverAHashOfEmpty(string? email)
    {
        var (md5, sha256) = AvatarEmailHasher.Compute(email);
        Assert.Null(md5);
        Assert.Null(sha256);
    }
}
