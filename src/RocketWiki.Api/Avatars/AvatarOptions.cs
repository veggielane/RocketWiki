using System.ComponentModel.DataAnnotations;

namespace RocketWiki.Api.Avatars;

/// <summary>
/// Avatar policy, bound from the <c>Avatars</c> configuration section (Program.cs).
/// </summary>
public sealed class AvatarOptions
{
    public const string SectionName = "Avatars";

    /// <summary>
    /// 5 MiB. The server normalizes uploads (decode → crop → resize → re-encode, so
    /// what is <i>stored</i> is always a small canonical PNG regardless of input),
    /// which makes direct phone-photo JPEGs a legitimate upload — most are under
    /// 5 MiB — while staying far below anything that needs streaming: the upload
    /// path buffers, deliberately, because decoding and hashing want the whole
    /// payload, and the processor's own dimension/memory limits bound the decode.
    /// </summary>
    public const long DefaultMaxSizeBytes = 5 * 1024 * 1024;

    /// <summary>Maximum accepted avatar upload in bytes. Exactly this size is
    /// accepted; one byte more is a 413 before any blob or row is written — the same
    /// layered enforcement as <c>Attachments:MaxSizeBytes</c>, and the same
    /// must-be-positive validation at startup.</summary>
    [Range(1, long.MaxValue, ErrorMessage = "Avatars:MaxSizeBytes must be a positive number of bytes.")]
    public long MaxSizeBytes { get; set; } = DefaultMaxSizeBytes;

    /// <summary>
    /// Whether <c>GET /avatar/{hash}</c> — the <b>unauthenticated</b>
    /// Gravatar/Libravatar-protocol endpoint for other in-network tools — answers at
    /// all. Default <b>false</b>, fail closed, the same posture as the OTLP endpoint,
    /// <c>VITE_DRAWIO_URL</c>, and <c>GitLab:BaseUrl</c> (design.md §15): serving
    /// anything without a token is this codebase's one deliberate exception to
    /// "all access requires sign-in", so it must be an operator's explicit decision,
    /// never a default. Disabled means the route 404s for every hash,
    /// indistinguishable from "no avatar". In-wiki avatar rendering
    /// (<c>GET /users/{id}/avatar</c>, authenticated) does not depend on this flag.
    /// </summary>
    public bool GravatarEndpointEnabled { get; set; }
}
