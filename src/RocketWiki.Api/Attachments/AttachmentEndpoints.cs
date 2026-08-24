using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.Attachments;

/// <summary>
/// The two non-GraphQL routes design.md §8/§10 calls for: attachment binary doesn't
/// belong inside a GraphQL response. Both run through the exact same identity
/// (ICurrentPrincipalAccessor/IActingUserAccessor), authorization (the services
/// themselves), and audit (IAuditSink / the domain-event pipeline) pipeline as
/// GraphQL — no side channel, no presigned URLs, ever.
/// </summary>
public static class AttachmentEndpoints
{
    public static WebApplication MapAttachmentEndpoints(this WebApplication app)
    {
        app.MapGet("/attachments/{id:guid}", DownloadAsync).RequireAuthorization();
        app.MapPost("/attachments/{pageId:guid}", UploadAsync).RequireAuthorization();

        return app;
    }

    [AuditAction("attachment.download")]
    private static async Task<IResult> DownloadAsync(
        Guid id,
        IAttachmentReadService attachmentReadService,
        ICurrentPrincipalAccessor principalAccessor,
        IAuditSink auditSink,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            // RequireAuthorization() already rejects a fully anonymous request with 401
            // before this handler runs; this covers the narrower case of an authenticated
            // token that still fails to yield a usable Principal (e.g. no `sub` claim).
            // Same "absent, not forbidden" rule as everywhere else (design.md §6.7)
            // applies either way - nothing distinguishes this from a genuine 404.
            return Results.NotFound();
        }

        var result = await attachmentReadService.DownloadAsync(id, principal, cancellationToken);

        switch (result)
        {
            case AttachmentDownloadResult.Found found:
                await auditSink.RecordAsync(
                    new AuditRecord("attachment.download", AuditOutcome.Success, AuditSubjectType.Attachment, found.Metadata.Id),
                    cancellationToken);
                // Uploader-supplied bytes under an uploader-supplied content type, so:
                // nosniff (no browser second-guesses the declared type), on top of the
                // fileDownloadName below (Content-Disposition: attachment - deliberate,
                // keeps the bytes from rendering in-page). Cache-Control is no-cache,
                // NOT a max-age: attachment reads are audited (§7), and a freshness
                // window would let repeat reads bypass the API - and with it canView
                // and the audit row. Every use revalidates here instead; the strong
                // ETag (the attachment id - blobs are immutable per id, a re-upload is
                // a new row) turns the unchanged case into a framework-handled 304
                // after the same authorization and audit as a 200.
                httpContext.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                httpContext.Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
                return Results.Stream(
                    found.Content, found.Metadata.ContentType, found.Metadata.FileName,
                    lastModified: null,
                    entityTag: new EntityTagHeaderValue($"\"{found.Metadata.Id:N}\""));

            case AttachmentDownloadResult.NotFound:
                return Results.NotFound();

            case AttachmentDownloadResult.Denied denied:
                // design.md §6.7: "indistinguishable to the caller, not to the audit
                // log" - the denial is recorded with the failing restriction (§7),
                // then this returns the byte-identical Results.NotFound() the case
                // above does. Any divergence between these two responses (status,
                // body, headers) would be exactly the existence leak §6.7 forbids;
                // DeniedReadAuditTests proves them equal at the HTTP level.
                await ReadDenialAudit.RecordAsync(
                    auditSink, "attachment.download", AuditSubjectType.Attachment,
                    denied.AttachmentId, denied.Reason, cancellationToken);
                return Results.NotFound();

            case AttachmentDownloadResult.BlobMissing blobMissing:
                // design.md §10: "surfaces as a flagged error, not a 500" - a raw
                // unhandled exception (a real, unstructured 500) would give an operator
                // nothing to go on. This is deliberately still a 500-range status
                // (something is genuinely, operationally wrong - the caller legitimately
                // can view this attachment, design.md says "nothing to hide here"), but
                // a *structured*, logged one: ProblemDetails plus a server-side log line
                // an operator can actually find and act on.
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RocketWiki.Api.Attachments")
                    .LogError(
                        "Attachment {AttachmentId} exists and is viewable but has no matching object in storage.",
                        blobMissing.Metadata.Id);
                return Results.Problem(
                    title: "Attachment content unavailable",
                    detail: "The attachment record exists but its stored content could not be found. This has been logged for operator review.",
                    statusCode: StatusCodes.Status500InternalServerError);

            default:
                throw new NotSupportedException($"Unhandled {nameof(AttachmentDownloadResult)} case '{result.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Room for the multipart envelope (boundary lines + part headers) around a file
    /// of exactly <c>MaxSizeBytes</c>, so the transport-level caps below don't refuse
    /// an at-limit upload the file-length check would accept. The generous 64 KiB
    /// keeps the transport bound coarse on purpose: the byte-precise, binding check
    /// is <c>file.Length &gt; MaxSizeBytes</c> in the handler.
    /// </summary>
    private const long MultipartEnvelopeAllowanceBytes = 64 * 1024;

    [AuditAction("attachment.upload")]
    private static async Task<IResult> UploadAsync(
        Guid pageId,
        HttpRequest request,
        IAttachmentService attachmentService,
        ICurrentPrincipalAccessor principalAccessor,
        IActingUserAccessor actingUserAccessor,
        ICurrentAuditContextAccessor auditContextAccessor,
        IAuditSink auditSink,
        IOptions<AttachmentOptions> attachmentOptions,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return Results.Json(unauthenticated, statusCode: StatusCodes.Status403Forbidden);
        }

        // --- Size limit (Attachments:MaxSizeBytes, the system's one declared cap;
        // see AttachmentOptions for why it exists and where its default comes from).
        // Enforced HERE, before the form is read - so before any blob write, DB row,
        // or even page lookup. Three layers derive from the same number:
        //   1. A declared Content-Length over the bound is refused up front, before
        //      the body is read at all - the common case, and the one that produces
        //      this route's structured 413.
        //   2. Kestrel's per-request body cap is re-pointed from its unrelated 30 MB
        //      global default to the same bound - the transport backstop for chunked
        //      requests that declare no Content-Length (its own 413 is bare, which is
        //      honest: nothing structured survives an aborted request body). Absent
        //      under TestServer, hence the null-conditional.
        //   3. The file part's actual length is the byte-precise, binding check
        //      below: > MaxSizeBytes refuses, == MaxSizeBytes is accepted.
        // Refusing a too-big upload writes NO audit row, same as the NotFound and
        // Validation refusals on this route (MutationAuthHelper's doc: only
        // permission-shaped failures are denials; §7's success|denied vocabulary has
        // no place for a request refused before any access decision was made - the
        // 413 fires identically for everyone, before the page is even loaded).
        var maxSizeBytes = attachmentOptions.Value.MaxSizeBytes;
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

        // Per-request FormOptions so the multipart reader's own section limit (a
        // global 128 MB default otherwise) also derives from the declared cap -
        // without this, an operator raising MaxSizeBytes past 128 MB would get an
        // unrelated 400 instead of the limit they configured.
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

        await using var content = file.OpenReadStream();
        var uploadRequest = new UploadAttachmentRequest(pageId, file.FileName, file.ContentType, content);
        var result = await attachmentService.UploadAsync(uploadRequest, principal!, actingUserId!.Value, auditContext!, cancellationToken);

        if (!result.IsSuccess)
        {
            // Success is already audited by the domain-event pipeline inside
            // AttachmentService (RaiseDomainEvent, same transaction) - only a denial
            // needs auditing explicitly here, same split as every GraphQL mutation.
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "attachment.upload", result.Error, AuditSubjectType.Attachment, subjectId: null, cancellationToken);
            var errorView = PageMutationErrorView.From(result.Error);
            return Results.Json(errorView, statusCode: MapErrorStatusCode(errorView.Kind));
        }

        var attachment = result.Value;
        return Results.Created($"/attachments/{attachment.Id}", new
        {
            attachment.Id,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes,
            attachment.CreatedAtUtc,
        });
    }

    /// <summary>Structured 413 (RFC 9110 "Content Too Large") as ProblemDetails.
    /// The configured limit rides along in an extension so a client can show the
    /// actual cap - it's deployment configuration, not content, so echoing it leaks
    /// nothing (§6.7/§15 concerns don't apply to a number every caller gets).</summary>
    private static IResult PayloadTooLarge(long maxSizeBytes) => Results.Problem(
        title: "Attachment too large",
        detail: $"The uploaded file exceeds the maximum attachment size of {maxSizeBytes} bytes.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        extensions: new Dictionary<string, object?> { ["maxSizeBytes"] = maxSizeBytes });

    private static int MapErrorStatusCode(string kind) => kind switch
    {
        "Forbidden" => StatusCodes.Status403Forbidden,
        "ReadOnlyReplica" => StatusCodes.Status403Forbidden,
        "NotFound" => StatusCodes.Status404NotFound,
        "Validation" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
