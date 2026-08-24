using Microsoft.AspNetCore.Http.Features;
using RocketWiki.Api.GraphQL;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.Http;

/// <summary>
/// The parts every binary-HTTP route shares (design.md §8/§10: attachment, avatar and
/// custom-emoji bytes stream through the API over plain HTTP, never GraphQL and never a
/// presigned URL). Three route classes previously carried three drifted copies of all of
/// this — a domain-error→status map that disagreed with itself on
/// <c>NameTaken</c>/<c>ReadOnlyReplica</c>, three near-identical 413 ProblemDetails, and
/// three transcriptions of the upload-cap layering. One copy now, so a new
/// <see cref="PageMutationError"/> subtype or a new binary route can only be wrong in one
/// place.
/// </summary>
internal static class BinaryRoutes
{
    /// <summary>Noun for this route's 413 title/detail. Deployment configuration and a
    /// fixed word — nothing caller-derived ever reaches these strings.</summary>
    internal const string AttachmentSubject = "Attachment";

    internal const string AvatarSubject = "Avatar";

    internal const string EmojiSubject = "Emoji image";

    /// <summary>
    /// Room for the multipart envelope (boundary lines + part headers) around a file of
    /// exactly the configured cap, so the transport-level bounds don't refuse an at-limit
    /// upload the file-length check would accept. The generous 64 KiB keeps the transport
    /// bound coarse on purpose: the byte-precise, binding check is the file's own length.
    /// Raw-body routes (no envelope) don't add it.
    /// </summary>
    internal const long MultipartEnvelopeAllowanceBytes = 64 * 1024;

    /// <summary>
    /// The one HTTP projection of <see cref="PageMutationErrorView.Kind"/>.
    /// <b><see cref="PageMutationErrorView"/> is the source of truth for the kind
    /// vocabulary</b> — add a case here the same commit a kind is added there; the
    /// default exists so an unmapped kind is a plain 400 rather than a 500, not as a
    /// licence to skip the case (BinaryRouteErrorMapTests sweeps every kind the view can
    /// produce).
    ///
    /// Which status each kind gets, and why:
    /// <list type="bullet">
    /// <item><c>Forbidden</c>, <c>SubtreeOperationForbidden</c>, <c>ReadOnlyReplica</c> →
    /// <b>403</b>. A replica-space refusal is an authorization refusal that no grant can
    /// lift (design.md §12), so it belongs with the other forbidden shapes rather than in
    /// the 400 bucket two of the three copies used to put it in.</item>
    /// <item><c>NotFound</c> → <b>404</b>.</item>
    /// <item><c>NameTaken</c>, <c>StaleRevision</c> → <b>409</b>. Both are conflicts with
    /// existing server state that the caller can resolve and retry, which is exactly RFC
    /// 9110's Conflict. <c>StaleRevision</c> is unreachable on every binary route today
    /// (none of them takes an expected revision number) — it is mapped for totality, not
    /// because a route can produce it.</item>
    /// <item><c>Validation</c> and anything unmapped → <b>400</b>.</item>
    /// </list>
    /// </summary>
    internal static int ErrorStatusCode(string kind) => kind switch
    {
        "Forbidden" => StatusCodes.Status403Forbidden,
        "SubtreeOperationForbidden" => StatusCodes.Status403Forbidden,
        "ReadOnlyReplica" => StatusCodes.Status403Forbidden,
        "NotFound" => StatusCodes.Status404NotFound,
        "NameTaken" => StatusCodes.Status409Conflict,
        "StaleRevision" => StatusCodes.Status409Conflict,
        "Validation" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>The flattened error view every binary route returns, under the status
    /// <see cref="ErrorStatusCode"/> gives its kind. Same body shape as the GraphQL
    /// payload's <c>error</c> field, so a client switches on <c>kind</c> either way.</summary>
    internal static IResult ErrorResult(PageMutationError error)
    {
        var view = PageMutationErrorView.From(error);
        return Results.Json(view, statusCode: ErrorStatusCode(view.Kind));
    }

    /// <summary>Structured 413 (RFC 9110 "Content Too Large") as ProblemDetails. The
    /// configured limit rides along in an extension so a client can show the actual cap —
    /// it is deployment configuration, not content, so echoing it leaks nothing
    /// (§6.7/§15 concerns don't apply to a number every caller gets identically). The SPA
    /// reads exactly this extension (web/src/http/authedFetch.ts).</summary>
    internal static IResult PayloadTooLarge(string subject, long maxSizeBytes) => Results.Problem(
        title: $"{subject} too large",
        detail: $"The upload exceeds the maximum {subject.ToLowerInvariant()} size of {maxSizeBytes} bytes.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        extensions: new Dictionary<string, object?> { ["maxSizeBytes"] = maxSizeBytes });

    /// <summary>
    /// Layers 1 and 2 of the upload cap, for a route that reads the body itself rather
    /// than through <see cref="ReadCappedMultipartFileAsync"/>:
    /// <list type="number">
    /// <item>A declared <c>Content-Length</c> over the bound is refused up front, before
    /// the body is read at all — the common case, and the one that produces the
    /// structured 413.</item>
    /// <item>Kestrel's per-request body cap is re-pointed from its unrelated 30 MB global
    /// default to the same bound — the transport backstop for chunked requests that
    /// declare no Content-Length (its own 413 is bare, which is honest: nothing structured
    /// survives an aborted request body). <b>Absent under TestServer</b>, hence the
    /// null-conditional — which is exactly why layer 3 (the byte-precise in-handler read
    /// bound) is not optional.</item>
    /// </list>
    /// Returns the 413 to return, or null to continue. Refusing an oversized upload writes
    /// NO audit row on any of these routes: §7's success|denied vocabulary has no place for
    /// a request refused before any access decision was made, and the refusal fires
    /// identically for everyone.
    /// </summary>
    internal static IResult? RefuseDeclaredOversize(
        HttpRequest request, string subject, long maxSizeBytes, long transportBound)
    {
        if (request.ContentLength is { } declaredLength && declaredLength > transportBound)
        {
            return PayloadTooLarge(subject, maxSizeBytes);
        }

        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = transportBound;
        }

        return null;
    }

    /// <summary>
    /// All three cap layers for a multipart upload route, plus the two request-shape
    /// refusals both such routes make identically. Returns either the single uploaded file
    /// or the <see cref="IResult"/> to return instead — never both, never neither.
    ///
    /// Layer 3 is the pair of in-handler bounds: <see cref="FormOptions.MultipartBodyLengthLimit"/>
    /// stops the reader itself at the transport bound (this is the multipart equivalent of
    /// the raw-body route's copy-at-most loop, and it is what protects a chunked upload
    /// under TestServer, where layer 2's Kestrel feature does not exist), and the file's own
    /// length is the byte-precise, binding check: <c>&gt; maxSizeBytes</c> refuses,
    /// <c>== maxSizeBytes</c> is accepted. Setting the reader's limit from the same number
    /// also keeps an operator who raises the cap past the framework's unrelated 128 MB
    /// section default from getting a mystery 400 instead of the limit they configured.
    /// </summary>
    internal static async Task<(IFormFile? File, IResult? Refusal)> ReadCappedMultipartFileAsync(
        HttpRequest request, string subject, long maxSizeBytes, CancellationToken cancellationToken)
    {
        var transportBound = maxSizeBytes + MultipartEnvelopeAllowanceBytes;

        if (RefuseDeclaredOversize(request, subject, maxSizeBytes, transportBound) is { } declaredRefusal)
        {
            return (null, declaredRefusal);
        }

        if (!request.HasFormContentType)
        {
            return (null, Results.BadRequest(new { message = "Expected multipart/form-data with a single file field." }));
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(
                new FormOptions { MultipartBodyLengthLimit = transportBound }, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // The reader hit MultipartBodyLengthLimit mid-stream (a chunked body that
            // declared no Content-Length). Same structured 413 as the up-front refusal
            // rather than the unhandled 500 that exception used to produce.
            return (null, PayloadTooLarge(subject, maxSizeBytes));
        }

        var file = form.Files.Count > 0 ? form.Files[0] : null;
        if (file is null || file.Length == 0)
        {
            return (null, Results.BadRequest(new { message = "No file provided." }));
        }

        return file.Length > maxSizeBytes
            ? (null, PayloadTooLarge(subject, maxSizeBytes))
            : (file, null);
    }

    /// <summary>
    /// Layer 3 for a raw-body upload route (one file, one route, no multipart envelope):
    /// copies at most <c>maxSizeBytes + 1</c> bytes, so a chunked upload that declares no
    /// Content-Length cannot buffer past the limit before being refused — the in-handler
    /// bound that does not depend on layer 2's Kestrel feature being present. Callers run
    /// <see cref="RefuseDeclaredOversize"/> first, at whatever point in their handler the
    /// cheap up-front refusal belongs.
    /// </summary>
    internal static async Task<(byte[]? Bytes, IResult? Refusal)> ReadCappedRawBodyAsync(
        HttpRequest request, string subject, long maxSizeBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await CopyAtMostAsync(request.Body, buffer, maxSizeBytes + 1, cancellationToken);

        return buffer.Length > maxSizeBytes
            ? (null, PayloadTooLarge(subject, maxSizeBytes))
            : (buffer.ToArray(), null);
    }

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
