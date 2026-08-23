using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Content;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.Avatars;

/// <summary>
/// Profile-picture routes — plain HTTP binary, following <see cref="Attachments.AttachmentEndpoints"/>'s
/// pattern (design.md §8/§10): bytes always stream through the API, no presigned
/// URLs, same identity pipeline. Four routes in two trust tiers:
///
/// <list type="bullet">
/// <item><c>POST /avatars</c> / <c>DELETE /avatars</c> — authenticated,
/// structurally self-only (no target user parameter exists; the acting user is the
/// only possible subject). Audited via the domain-event pipeline
/// (<c>settings.avatar.set</c>/<c>.cleared</c>), on the <c>attachment</c> channel —
/// deliberately reused rather than a new enum value, because that channel names the
/// binary-HTTP surface these routes share with attachments, and the action names
/// carry the distinction.</item>
/// <item><c>GET /users/{id}/avatar</c> — authenticated, any user's avatar. Emits
/// <b>no</b> audit row, decided rather than overlooked: an avatar is display data
/// exactly like the display name <c>UserRef</c> resolves for comment bylines, and
/// display-name resolution has never emitted rows of its own (design.md §8's
/// nested-field rule) — rendering an avatar in a comment list is not a content read,
/// and auditing it would flood §7's table with one machine row per face per page
/// while recording no access decision (there is no per-avatar rule to pass or
/// fail).</item>
/// <item><c>GET /avatar/{hash}</c> — the Gravatar/Libravatar-protocol endpoint,
/// <b>anonymous by design</b> when <c>Avatars:GravatarEndpointEnabled</c> is true
/// (default false, fail closed — see <see cref="AvatarOptions"/>). Deliberately
/// mapped WITHOUT RequireAuthorization; JIT provisioning skips unauthenticated
/// requests, no audit sink is ever touched on this path (there is no acting user to
/// attribute a row to, and DbAuditSink correctly refuses exactly that), and the only
/// telemetry is a bounded hit/miss/disabled counter — never a hash. Protocol
/// pragmatics, stated: <c>d=404</c> semantics are the ONLY behavior (unknown hash,
/// avatar-less user, or feature disabled all 404, indistinguishable); every other
/// <c>d=</c> value is ignored — no server-side identicon generation, the consumer's
/// own fallback handles a 404. <c>s=</c>/<c>size=</c> is honored: clamped to
/// [<see cref="MinGravatarSize"/>, 512] and resized <b>on demand</b> from the stored
/// canonical 512 (absent/unparseable means the stored 512, and any size is a
/// downscale — never an upscale past canonical). On-demand over pre-generated
/// variants, deliberately: a 512→N downscale of trusted bytes is milliseconds,
/// consumers pass arbitrary sizes so variants would either miss or sprawl,
/// derived objects in storage would need their own janitor/invalidation story, and
/// HTTP caching (strong ETag per (content, size) + max-age) already absorbs the
/// repeat traffic an in-network consumer generates.</item>
/// </list>
///
/// Serving is PNG-only by construction — storage only ever holds the
/// server-re-encoded canonical PNG (see <c>IAvatarImageProcessor</c>: uploads may be
/// PNG, JPEG, or WebP; they are decoded under hard limits, center-cropped, resized,
/// re-encoded, and the original bytes are never stored; SVG is never accepted,
/// scripting risk) — with <c>X-Content-Type-Options: nosniff</c> so no browser
/// second-guesses the type, and an ETag on the stored content hash so avatar-heavy
/// views revalidate as 304s.
/// </summary>
public static class AvatarEndpoints
{
    public static WebApplication MapAvatarEndpoints(this WebApplication app)
    {
        app.MapPost("/avatars", SetAsync).RequireAuthorization();
        app.MapDelete("/avatars", ClearAsync).RequireAuthorization();
        app.MapGet("/users/{id:guid}/avatar", GetUserAvatarAsync).RequireAuthorization();

        // Anonymous by design — see the class doc. The route is mapped even when the
        // flag is off so enabling it is pure configuration; disabled means 404 for
        // everything, indistinguishable from "no avatar".
        app.MapGet("/avatar/{hash}", GetByEmailHashAsync);

        return app;
    }

    /// <summary>Same purpose and value as the attachment route's allowance: room for
    /// the multipart envelope around a file of exactly MaxSizeBytes, with the
    /// byte-precise check being the file length itself.</summary>
    private const long MultipartEnvelopeAllowanceBytes = 64 * 1024;

    private static async Task<IResult> SetAsync(
        HttpRequest request,
        IUserAvatarService avatarService,
        ICurrentPrincipalAccessor principalAccessor,
        IActingUserAccessor actingUserAccessor,
        ICurrentAuditContextAccessor auditContextAccessor,
        IOptions<AvatarOptions> avatarOptions,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return Results.Json(unauthenticated, statusCode: StatusCodes.Status403Forbidden);
        }

        // Size cap: the same three-layer derivation as attachment uploads (declared
        // Content-Length up front, transport body cap, byte-precise file length),
        // from Avatars:MaxSizeBytes instead. A refused-too-big upload writes no audit
        // row - no access decision was made (design.md §7's outcome vocabulary).
        var maxSizeBytes = avatarOptions.Value.MaxSizeBytes;
        var transportBound = maxSizeBytes + MultipartEnvelopeAllowanceBytes;

        if (request.ContentLength is { } declaredLength && declaredLength > transportBound)
        {
            return PayloadTooLarge(maxSizeBytes);
        }

        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = transportBound;
        }

        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Expected multipart/form-data with a single file field." });
        }

        var form = await request.ReadFormAsync(
            new FormOptions { MultipartBodyLengthLimit = transportBound }, cancellationToken);
        var file = form.Files.Count > 0 ? form.Files[0] : null;
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "No file provided." });
        }

        if (file.Length > maxSizeBytes)
        {
            return PayloadTooLarge(maxSizeBytes);
        }

        // Fully buffered on purpose: the cap above bounds this (5 MiB default), and
        // decoding + content hashing need the whole payload. The processor's own
        // dimension/memory limits bound what decoding this buffer can cost.
        byte[] bytes;
        await using (var content = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await content.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var result = await avatarService.SetAsync(
            new SetUserAvatarRequest(bytes), actingUserId!.Value, auditContext!, cancellationToken);

        // The only failure shape is Validation (self-only by construction leaves no
        // permission to deny), which §7 excludes from audit - same as the GitLab
        // settings mutations. Success is audited by the domain-event pipeline inside
        // the service, in the same transaction as the row.
        return result.IsSuccess
            ? Results.Ok(new { hasAvatar = true })
            : ErrorResult(result.Error);
    }

    private static async Task<IResult> ClearAsync(
        IUserAvatarService avatarService,
        ICurrentPrincipalAccessor principalAccessor,
        IActingUserAccessor actingUserAccessor,
        ICurrentAuditContextAccessor auditContextAccessor,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return Results.Json(unauthenticated, statusCode: StatusCodes.Status403Forbidden);
        }

        var result = await avatarService.ClearAsync(actingUserId!.Value, auditContext!, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(new { hasAvatar = false })
            : ErrorResult(result.Error);
    }

    private static async Task<IResult> GetUserAvatarAsync(
        Guid id,
        IUserAvatarService avatarService,
        ICurrentPrincipalAccessor principalAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Same guard as the attachment download: RequireAuthorization already 401s
        // the fully anonymous; this covers an authenticated token yielding no usable
        // Principal, collapsed to the same 404 as everything absent.
        if (principalAccessor.Current is null)
        {
            return Results.NotFound();
        }

        var result = await avatarService.OpenAsync(id, cancellationToken);
        switch (result)
        {
            case UserAvatarReadResult.Found found:
                // Private: an authenticated, per-viewer response. max-age keeps a
                // browsing session from even revalidating per comment row; the ETag
                // turns the revalidations that do happen into 304s.
                return PngResult(found, httpContext, "private, max-age=300");

            case UserAvatarReadResult.NotFound:
                return Results.NotFound();

            case UserAvatarReadResult.BlobMissing blobMissing:
                // design.md §10's flagged-error rule, same as attachments: the row
                // exists, so a bare 404 would lie and an unstructured 500 would help
                // nobody. Nothing here is restricted - "nothing to hide".
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RocketWiki.Api.Avatars")
                    .LogError("Avatar for user {UserId} exists but has no matching object in storage.", blobMissing.UserId);
                return Results.Problem(
                    title: "Avatar content unavailable",
                    detail: "The avatar record exists but its stored content could not be found. This has been logged for operator review.",
                    statusCode: StatusCodes.Status500InternalServerError);

            default:
                throw new NotSupportedException($"Unhandled {nameof(UserAvatarReadResult)} case '{result.GetType().Name}'.");
        }
    }

    /// <summary>Smallest honored <c>s=</c>. Below this a resize produces unusable
    /// mush; consumers asking for less get this and scale down themselves.</summary>
    private const int MinGravatarSize = 16;

    private static async Task<IResult> GetByEmailHashAsync(
        string hash,
        IUserAvatarService avatarService,
        IAvatarImageProcessor imageProcessor,
        IOptions<AvatarOptions> avatarOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Fail closed (AvatarOptions): off means 404 for every hash - the response an
        // unknown hash gets, so a probe can't even learn whether the feature is on.
        if (!avatarOptions.Value.GravatarEndpointEnabled)
        {
            ApiTelemetry.RecordGravatarRequest(ApiTelemetry.GravatarOutcomeDisabled);
            return Results.NotFound();
        }

        // s=/size= is honored, clamped (class doc); d=/default= is deliberately
        // never read - 404 IS the default behavior. No identicon generation, no
        // redirects.
        var size = ParseRequestedSize(httpContext.Request.Query);

        var result = await avatarService.OpenByEmailHashAsync(hash, cancellationToken);
        switch (result)
        {
            case UserAvatarReadResult.Found found:
                ApiTelemetry.RecordGravatarRequest(ApiTelemetry.GravatarOutcomeHit);
                // Public: the endpoint is anonymous by design, so a shared cache
                // holding the bytes discloses nothing the origin wouldn't; 5 minutes
                // matches gravatar.com's own practice.
                if (size == PngHeader.AvatarDimension)
                {
                    return PngResult(found, httpContext, "public, max-age=300");
                }

                // On-demand downscale of the canonical stored PNG (trusted bytes this
                // system produced). Strong ETag per (content, size): a consumer that
                // cached s=80 revalidates s=80, not the 512.
                byte[] canonical;
                await using (found.Content)
                using (var buffer = new MemoryStream((int)found.SizeBytes))
                {
                    await found.Content.CopyToAsync(buffer, cancellationToken);
                    canonical = buffer.ToArray();
                }

                var resized = imageProcessor.ResizeCanonicalPng(canonical, size);
                httpContext.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                httpContext.Response.Headers[HeaderNames.CacheControl] = "public, max-age=300";
                var resizedTag = new EntityTagHeaderValue(
                    $"\"{Convert.ToHexStringLower(found.ContentHash)}-{size}\"");
                return Results.Bytes(resized, "image/png", fileDownloadName: null,
                    lastModified: null, entityTag: resizedTag);

            case UserAvatarReadResult.NotFound:
                ApiTelemetry.RecordGravatarRequest(ApiTelemetry.GravatarOutcomeMiss);
                return Results.NotFound();

            case UserAvatarReadResult.BlobMissing blobMissing:
                // Unlike the authenticated route's structured 500: this protocol's
                // consumers treat any non-200 as "use your fallback", so 404 is the
                // honest wire answer - but the fault is still logged for operators.
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RocketWiki.Api.Avatars")
                    .LogError("Avatar for user {UserId} exists but has no matching object in storage (gravatar route).", blobMissing.UserId);
                ApiTelemetry.RecordGravatarRequest(ApiTelemetry.GravatarOutcomeMiss);
                return Results.NotFound();

            default:
                throw new NotSupportedException($"Unhandled {nameof(UserAvatarReadResult)} case '{result.GetType().Name}'.");
        }
    }

    /// <summary>Clamp of <c>s=</c>/<c>size=</c> (<c>s</c> wins when both appear,
    /// matching Gravatar's own parameter precedence being irrelevant — consumers send
    /// one): absent or unparseable means canonical; anything else lands in
    /// [<see cref="MinGravatarSize"/>, canonical]. Upscales past canonical are
    /// clamped down — the stored 512 is the most detail that exists.</summary>
    private static int ParseRequestedSize(IQueryCollection query)
    {
        var raw = query.TryGetValue("s", out var s) && !string.IsNullOrEmpty(s.ToString()) ? s.ToString()
            : query.TryGetValue("size", out var sizeValue) ? sizeValue.ToString()
            : null;

        if (raw is null || !int.TryParse(raw, out var parsed))
        {
            return PngHeader.AvatarDimension;
        }

        return Math.Clamp(parsed, MinGravatarSize, PngHeader.AvatarDimension);
    }

    /// <summary>Shared success shape for the canonical-size responses: fixed
    /// image/png (storage only ever holds the server-re-encoded canonical PNG - no
    /// sniffing, and nosniff says so), ETag from the stored content hash with
    /// If-None-Match handled by the framework's precondition processing (304s carry
    /// the same headers).</summary>
    private static IResult PngResult(UserAvatarReadResult.Found found, HttpContext httpContext, string cacheControl)
    {
        httpContext.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        httpContext.Response.Headers[HeaderNames.CacheControl] = cacheControl;

        var etag = new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(found.ContentHash)}\"");
        return Results.Stream(found.Content, "image/png", fileDownloadName: null, lastModified: null, entityTag: etag);
    }

    private static IResult ErrorResult(PageMutationError error)
    {
        var errorView = PageMutationErrorView.From(error);
        return Results.Json(errorView, statusCode: errorView.Kind switch
        {
            "Forbidden" => StatusCodes.Status403Forbidden,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        });
    }

    private static IResult PayloadTooLarge(long maxSizeBytes) => Results.Problem(
        title: "Avatar too large",
        detail: $"The uploaded file exceeds the maximum avatar size of {maxSizeBytes} bytes.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        extensions: new Dictionary<string, object?> { ["maxSizeBytes"] = maxSizeBytes });
}
