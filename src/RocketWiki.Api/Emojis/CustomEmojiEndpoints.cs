using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Content;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Storage;

namespace RocketWiki.Api.Emojis;

/// <summary>
/// The custom-emoji binary routes, patterned on <see cref="Attachments.AttachmentEndpoints"/>:
/// plain HTTP because image bytes don't belong in GraphQL responses, running through the
/// same identity, authorization, and audit pipeline — no side channel, no presigned URLs.
///
/// Registry mutations (POST/DELETE) are instance-admin only (design.md §6.5: the
/// registry is instance vocabulary, like the attribute registry). The serve route (GET)
/// requires only authentication: an emoji is a display asset every signed-in user's
/// renderer needs, the same reasoning that leaves avatar/display-name resolution
/// unaudited — so GETs write no audit rows, while every mutation (and every refused
/// mutation) does, via the domain-event pipeline / the explicit denial row below.
///
/// Everything here resolves from services other features already registered
/// (DbContext, IFileStorage, IInstanceRoleAccessor, IAuditSink) — the feature's whole
/// Program.cs footprint is the single MapCustomEmojiEndpoints call.
/// </summary>
public static class CustomEmojiEndpoints
{
    public static WebApplication MapCustomEmojiEndpoints(this WebApplication app)
    {
        app.MapGet("/emojis/{name}", ServeAsync).RequireAuthorization();
        app.MapPost("/emojis/{name}", CreateAsync).RequireAuthorization();
        app.MapDelete("/emojis/{name}", DeleteAsync).RequireAuthorization();

        return app;
    }

    [AuditAction("emoji.created")]
    private static async Task<IResult> CreateAsync(
        string name,
        HttpRequest request,
        RocketWikiDbContext db,
        IFileStorage fileStorage,
        IInstanceRoleAccessor instanceRoleAccessor,
        ICurrentPrincipalAccessor principalAccessor,
        IActingUserAccessor actingUserAccessor,
        ICurrentAuditContextAccessor auditContextAccessor,
        IAuditSink auditSink,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return Results.Json(unauthenticated, statusCode: StatusCodes.Status403Forbidden);
        }

        // Size cap first (Emojis:MaxSizeBytes) - before the body is buffered, and long
        // before any decode. Unaudited, like the attachment 413: a refusal made before
        // any access decision has no place in §7's success|denied vocabulary.
        var maxSizeBytes = EmojiOptions.FromConfiguration(configuration).MaxSizeBytes;
        if (request.ContentLength is { } declaredLength && declaredLength > maxSizeBytes)
        {
            return PayloadTooLarge(maxSizeBytes);
        }

        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maxSizeBytes;
        }

        // Grammar next - cheap, content-free, and also unaudited (a malformed name is
        // a validation failure, not an access refusal). The service re-checks; this is
        // the fail-fast copy.
        if (!EmojiName.IsValid(name))
        {
            return ErrorResult(new ValidationError(
                $"Emoji names must match {EmojiName.Pattern} (lowercase letters, digits, '_' and '-'; 1-{EmojiName.MaxLength} characters)."));
        }

        // Admin gate BEFORE the body is read or decoded: image decoding is the
        // expensive, attack-surface step, and it must never run for a caller the
        // gate refuses. The denial is audited here with the same details shape
        // MutationAuthHelper.AuditDenialIfApplicableAsync gives a ForbiddenError -
        // plus the emoji name, since no AuditSubjectType fits the registry (§7's
        // subject list is wiki content shapes; same reasoning as the mapper's
        // emoji.created case).
        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            await AuditAdminDenialAsync(auditSink, "emoji.created", name, cancellationToken);
            return ErrorResult(new ForbiddenError("instance admin required"));
        }

        // Raw body, not multipart: one file, one route, no envelope needed. Copy is
        // capped at maxSizeBytes + 1 so a chunked upload with no Content-Length can't
        // buffer past the limit before being refused.
        byte[] uploadBytes;
        using (var buffer = new MemoryStream())
        {
            await CopyAtMostAsync(request.Body, buffer, maxSizeBytes + 1, cancellationToken);
            if (buffer.Length > maxSizeBytes)
            {
                return PayloadTooLarge(maxSizeBytes);
            }

            uploadBytes = buffer.ToArray();
        }

        if (uploadBytes.Length == 0)
        {
            return ErrorResult(new ValidationError("No image bytes provided. Send the image file as the raw request body."));
        }

        var processed = EmojiImageProcessor.Process(uploadBytes, maxSizeBytes);
        if (!processed.IsSuccess)
        {
            return ErrorResult(new ValidationError(processed.FailureReason!));
        }

        ICustomEmojiService service = new CustomEmojiService(db, fileStorage);
        var result = await service.CreateAsync(
            new CreateCustomEmojiRequest(name, processed.Result!.Bytes, processed.Result.ContentType, processed.Result.PixelSize),
            instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);

        if (!result.IsSuccess)
        {
            // Success is audited by the domain-event pipeline inside the service (same
            // transaction); the admin gate already audited the only permission-shaped
            // refusal above, so what reaches here (NameTaken, Validation) is unaudited
            // by the same rule as every mutation route.
            return ErrorResult(result.Error);
        }

        var emoji = result.Value;
        return Results.Created($"/emojis/{emoji.Name}", new
        {
            emoji.Name,
            emoji.ContentType,
            emoji.SizeBytes,
            emoji.PixelSize,
            Etag = EtagFor(emoji.ContentHash),
        });
    }

    [AuditAction("emoji.deleted")]
    private static async Task<IResult> DeleteAsync(
        string name,
        RocketWikiDbContext db,
        IFileStorage fileStorage,
        IInstanceRoleAccessor instanceRoleAccessor,
        ICurrentPrincipalAccessor principalAccessor,
        IActingUserAccessor actingUserAccessor,
        ICurrentAuditContextAccessor auditContextAccessor,
        IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return Results.Json(unauthenticated, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            await AuditAdminDenialAsync(auditSink, "emoji.deleted", name, cancellationToken);
            return ErrorResult(new ForbiddenError("instance admin required"));
        }

        ICustomEmojiService service = new CustomEmojiService(db, fileStorage);
        var result = await service.DeleteAsync(name, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);

        return result.IsSuccess ? Results.NoContent() : ErrorResult(result.Error);
    }

    /// <summary>
    /// Serves the stored (re-encoded) bytes. Cache behavior matters more here than on
    /// any other route — one emoji can render hundreds of times across documents — so:
    /// a strong ETag (the stored bytes' SHA-256, immutable per registry entry since
    /// delete+recreate changes it), If-None-Match → 304, and Cache-Control private
    /// with a day's max-age. The GraphQL customEmojis list exposes the same etag per
    /// name, which is the SPA's signal to revalidate or rebuild its blob cache.
    /// `nosniff` on every response: the bytes are admin-supplied and re-encoded, but
    /// declaring "this is exactly image/png|gif, believe nothing else" costs one header.
    /// </summary>
    [NoAudit("Display asset every signed-in renderer needs (design.md §7/§19): reading avatar or emoji images is deliberately unaudited on every path, same reasoning as display-name resolution — see the class doc.")]
    private static async Task<IResult> ServeAsync(
        string name,
        RocketWikiDbContext db,
        IFileStorage fileStorage,
        ICurrentPrincipalAccessor principalAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Same narrow case as AttachmentEndpoints.DownloadAsync: RequireAuthorization
        // already 401'd anonymous callers; this covers an authenticated token yielding
        // no usable Principal. 404 either way - nothing distinguishable.
        if (principalAccessor.Current is null)
        {
            return Results.NotFound();
        }

        // A grammar-invalid name can never exist in the registry, so it is the same
        // 404 as an unknown one - the serve route mirrors the renderer's rule that
        // an unknown ':name:' is simply not an emoji. Exact/ordinal lookup only.
        if (!EmojiName.IsValid(name))
        {
            return Results.NotFound();
        }

        var emoji = await db.CustomEmojis.FirstOrDefaultAsync(e => e.Name == name, cancellationToken);
        if (emoji is null)
        {
            return Results.NotFound();
        }

        var etag = EtagFor(emoji.ContentHash);
        var headers = httpContext.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = "private, max-age=86400";
        headers.XContentTypeOptions = "nosniff";

        if (MatchesEtag(httpContext.Request.Headers.IfNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (!await fileStorage.ExistsAsync(emoji.StorageKey, cancellationToken))
        {
            // Same flagged-error stance as the attachment BlobMissing case (design.md
            // §10): a row whose object is gone is an operational fault worth a log
            // line and a structured 500, never a bare unhandled exception.
            httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("RocketWiki.Api.Emojis")
                .LogError("Custom emoji {EmojiId} exists but has no matching object in storage.", emoji.Id);
            return Results.Problem(
                title: "Emoji content unavailable",
                detail: "The emoji record exists but its stored image could not be found. This has been logged for operator review.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var content = await fileStorage.OpenReadAsync(emoji.StorageKey, cancellationToken);
        return Results.Stream(content, emoji.ContentType);
    }

    /// <summary>Strong-ETag comparison against If-None-Match (RFC 9110 §13.1.2):
    /// any listed tag equal to ours, or `*`, is a hit.</summary>
    private static bool MatchesEtag(StringValues ifNoneMatch, string etag)
    {
        foreach (var headerValue in ifNoneMatch)
        {
            if (headerValue is null)
            {
                continue;
            }

            foreach (var candidate in headerValue.Split(','))
            {
                var trimmed = candidate.Trim();
                if (trimmed == "*" || trimmed == etag)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string EtagFor(byte[] contentHash) => $"\"{Convert.ToHexStringLower(contentHash)}\"";

    /// <summary>The mutation-denial audit row for the instance-admin gate — the
    /// details shape MutationAuthHelper gives a ForbiddenError, widened with the emoji
    /// name (the registry has no AuditSubjectType; the name doubles as the DedupKey
    /// so two different names refused in one request both get their row).</summary>
    private static Task AuditAdminDenialAsync(IAuditSink auditSink, string action, string name, CancellationToken ct) =>
        auditSink.RecordAsync(new AuditRecord(
            action, AuditOutcome.Denied,
            DetailsJson: JsonSerializer.Serialize(new { reason = "instance admin required", name }),
            DedupKey: name), ct);

    private static IResult ErrorResult(PageMutationError error)
    {
        var view = PageMutationErrorView.From(error);
        return Results.Json(view, statusCode: view.Kind switch
        {
            "Forbidden" => StatusCodes.Status403Forbidden,
            "NotFound" => StatusCodes.Status404NotFound,
            "NameTaken" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        });
    }

    private static IResult PayloadTooLarge(long maxSizeBytes) => Results.Problem(
        title: "Emoji image too large",
        detail: $"The uploaded image exceeds the maximum emoji size of {maxSizeBytes} bytes.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        extensions: new Dictionary<string, object?> { ["maxSizeBytes"] = maxSizeBytes });

    private static async Task CopyAtMostAsync(Stream source, MemoryStream destination, long limit, CancellationToken ct)
    {
        var rented = new byte[64 * 1024];
        while (destination.Length < limit)
        {
            var toRead = (int)Math.Min(rented.Length, limit - destination.Length);
            var read = await source.ReadAsync(rented.AsMemory(0, toRead), ct);
            if (read == 0)
            {
                return;
            }

            destination.Write(rented, 0, read);
        }
    }
}
