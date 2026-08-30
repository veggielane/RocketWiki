using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Http;
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

        // Resolve = authorize + metadata, WITHOUT touching storage. The conditional-request
        // check below then happens after authorization and after the audit row, but before
        // any blob is opened. Order matters in both directions: a 304 that skipped
        // canView or the audit row would be a hole (§6.7/§7), and opening the object
        // before evaluating If-None-Match — which is what this did — meant every repeat
        // view paid an S3 HEAD plus a full GET whose bytes were then discarded. That is
        // the common case here, not a rare one, precisely because Cache-Control is
        // no-cache and every reuse is required to revalidate.
        var result = await attachmentReadService.ResolveForDownloadAsync(id, principal, cancellationToken);

        switch (result)
        {
            case AttachmentAccessResult.Allowed allowed:
                await auditSink.RecordAsync(
                    new AuditRecord("attachment.download", AuditOutcome.Success, AuditSubjectType.Attachment, allowed.Metadata.Id),
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

                var entityTag = new EntityTagHeaderValue($"\"{allowed.Metadata.Id:N}\"");
                if (IfNoneMatchSatisfied(httpContext.Request, entityTag))
                {
                    // Answered without opening the object at all. The framework would
                    // have produced the same 304 from Results.Stream — but only after
                    // the bytes had been fetched and thrown away.
                    httpContext.Response.Headers[HeaderNames.ETag] = entityTag.ToString();
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                var content = await attachmentReadService.OpenContentAsync(allowed.Metadata, cancellationToken);
                if (content is null)
                {
                    return BlobMissing(httpContext, allowed.Metadata);
                }

                return Results.Stream(
                    content, allowed.Metadata.ContentType, allowed.Metadata.FileName,
                    lastModified: null,
                    entityTag: entityTag);

            case AttachmentAccessResult.NotFound:
                return Results.NotFound();

            case AttachmentAccessResult.Denied denied:
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

            default:
                throw new NotSupportedException($"Unhandled {nameof(AttachmentAccessResult)} case '{result.GetType().Name}'.");
        }
    }

    /// <summary>
    /// design.md §10: "surfaces as a flagged error, not a 500" — a raw unhandled
    /// exception (a real, unstructured 500) would give an operator nothing to go on.
    /// Deliberately still a 500-range status (something is genuinely, operationally
    /// wrong, and the caller legitimately can view this attachment, so design.md says
    /// "nothing to hide here"), but a STRUCTURED, logged one: ProblemDetails plus a
    /// server-side log line an operator can actually find and act on.
    /// </summary>
    private static IResult BlobMissing(HttpContext httpContext, Core.Entities.Attachment metadata)
    {
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RocketWiki.Api.Attachments")
            .LogError(
                "Attachment {AttachmentId} exists and is viewable but has no matching object in storage.",
                metadata.Id);

        return Results.Problem(
            title: "Attachment content unavailable",
            detail: "The attachment record exists but its stored content could not be found. This has been logged for operator review.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    /// <summary>
    /// RFC 9110 §13.1.2: If-None-Match matches on a WEAK comparison, and "*" matches any
    /// existing representation. Hand-rolled rather than delegating to Results.Stream
    /// because the whole point is to answer before the object is opened; the framework
    /// still re-evaluates the same header on the 200 path, so the two cannot disagree
    /// about what a match is.
    /// </summary>
    private static bool IfNoneMatchSatisfied(HttpRequest request, EntityTagHeaderValue entityTag)
    {
        var header = request.Headers[HeaderNames.IfNoneMatch];
        if (header.Count == 0 || !EntityTagHeaderValue.TryParseList(header, out var candidates) || candidates is null)
        {
            return false;
        }

        return candidates.Any(candidate =>
            candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(entityTag, useStrongComparison: false));
    }

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
        // or even page lookup. All three layers (declared Content-Length, the
        // transport backstop, the byte-precise in-handler bound) derive from this one
        // number inside BinaryRoutes, which the avatar route shares verbatim.
        // Refusing a too-big upload writes NO audit row, same as the NotFound and
        // Validation refusals on this route (MutationAuthHelper's doc: only
        // permission-shaped failures are denials; §7's success|denied vocabulary has
        // no place for a request refused before any access decision was made - the
        // 413 fires identically for everyone, before the page is even loaded).
        var maxSizeBytes = attachmentOptions.Value.MaxSizeBytes;
        var (file, refusal) = await BinaryRoutes.ReadCappedMultipartFileAsync(
            request, BinaryRoutes.AttachmentSubject, maxSizeBytes, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        await using var content = file!.OpenReadStream();
        var uploadRequest = new UploadAttachmentRequest(pageId, file.FileName, file.ContentType, content);
        var result = await attachmentService.UploadAsync(uploadRequest, principal!, actingUserId!.Value, auditContext!, cancellationToken);

        if (!result.IsSuccess)
        {
            // Success is already audited by the domain-event pipeline inside
            // AttachmentService (RaiseDomainEvent, same transaction) - only a denial
            // needs auditing explicitly here, same split as every GraphQL mutation.
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "attachment.upload", result.Error, AuditSubjectType.Attachment, subjectId: null, cancellationToken);

            // Reachable kinds here are NotFound (404), ReadOnlyReplica (403),
            // Forbidden (403) and Validation (400) - see AttachmentService.UploadAsync.
            // NameTaken, StaleRevision and SubtreeOperationForbidden are structurally
            // unreachable on this route; BinaryRoutes maps them anyway so the three
            // binary routes cannot drift apart again.
            return BinaryRoutes.ErrorResult(result.Error);
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
}
