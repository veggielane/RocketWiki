namespace RocketWiki.Core.Services;

/// <summary>Outcome of normalizing an untrusted upload into the canonical avatar
/// form. <see cref="Invalid"/> messages describe the refusal category only — they
/// never echo submitted bytes.</summary>
public abstract record AvatarImageResult
{
    /// <summary>The canonical stored form: a freshly re-encoded 512×512 PNG. Never
    /// the caller's original bytes.</summary>
    public sealed record Ok(byte[] Png) : AvatarImageResult;

    public sealed record Invalid(string Message) : AvatarImageResult;
}

/// <summary>
/// Image normalization for profile pictures, behind an interface for the same reason
/// <c>ISearchService</c>/<c>IFileStorage</c> exist: the concrete implementation
/// (ImageSharp, in RocketWiki.Api) is a third-party dependency the service layer
/// shouldn't be married to. The contract is the load-bearing part:
///
/// <list type="bullet">
/// <item><b>Accepted input:</b> PNG, JPEG, or WebP — decoded with hard limits
/// (single frame, capped dimensions, capped decode memory) because upload bytes are
/// untrusted. Anything else — SVG above all (scripting risk), but also any format
/// outside the allow-list — is <see cref="AvatarImageResult.Invalid"/>.</item>
/// <item><b>Output:</b> always a server-produced 512×512 PNG: center-cropped to
/// square, resized, and <b>re-encoded</b> — the user's original bytes are never
/// stored. Re-encoding strips EXIF/XMP/IPTC/ICC wholesale (camera metadata such as
/// GPS position never reaches storage — a privacy property, since avatars are the
/// most widely served bytes in the system) and forecloses polyglot-file tricks: a
/// file crafted to be both a valid image and something else does not survive a
/// decode/re-encode round trip.</item>
/// </list>
/// </summary>
public interface IAvatarImageProcessor
{
    AvatarImageResult Normalize(byte[] upload);

    /// <summary>
    /// Downscales the <b>canonical stored PNG</b> (trusted — this system produced
    /// it) to <paramref name="size"/>×<paramref name="size"/> for the Gravatar
    /// endpoint's <c>s=</c> parameter. Callers clamp <paramref name="size"/> to the
    /// documented range first; passing the canonical dimension is a logic error
    /// (serve the stored bytes instead of re-encoding them).
    /// </summary>
    byte[] ResizeCanonicalPng(byte[] canonicalPng, int size);
}
