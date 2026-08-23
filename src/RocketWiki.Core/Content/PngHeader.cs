using System.Buffers.Binary;

namespace RocketWiki.Core.Content;

/// <summary>
/// Hand-rolled PNG signature + IHDR parsing. Uploads themselves are normalized by
/// <c>IAvatarImageProcessor</c> (ImageSharp: decode with limits, center-crop, resize,
/// re-encode) — this class is the <b>library-independent output invariant</b>: before
/// anything is stored, the re-encoded bytes must read back as a well-formed PNG
/// (right magic, IHDR first as the spec requires) declaring exactly
/// <see cref="AvatarDimension"/> square. Verifying our own output with our own ~30
/// lines means an ImageSharp upgrade that changed encoding behavior fails loudly at
/// upload rather than quietly storing a non-canonical object. Deliberately no
/// content sniffing anywhere: what is stored is what was verified, and it is served
/// as <c>image/png</c> with <c>X-Content-Type-Options: nosniff</c>.
/// </summary>
public static class PngHeader
{
    /// <summary>The fixed canonical avatar edge length. A contract constant, not
    /// configuration: every stored avatar is exactly this, and consumers either
    /// downscale or ask the Gravatar route's <c>s=</c> for less.</summary>
    public const int AvatarDimension = 512;

    // PNG spec §5.2: the eight-byte file signature.
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Signature (8) + IHDR chunk length (4) + "IHDR" (4) + width (4) + height (4).
    private const int MinBytesForDimensions = 24;
    private const int IhdrDataLength = 13;

    /// <summary>True iff <paramref name="bytes"/> starts with the PNG signature
    /// followed by a correctly declared IHDR chunk; outputs its width/height.</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (bytes.Length < MinBytesForDimensions || !bytes[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }

        // First chunk must be IHDR with a 13-byte payload (PNG spec §11.2.1).
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != IhdrDataLength
            || !bytes[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>The avatar contract in one call: a real PNG header declaring exactly
    /// <see cref="AvatarDimension"/> square.</summary>
    public static bool IsAvatarPng(ReadOnlySpan<byte> bytes) =>
        TryReadDimensions(bytes, out var width, out var height)
        && width == AvatarDimension
        && height == AvatarDimension;
}
