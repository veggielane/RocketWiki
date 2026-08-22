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
                return Results.Stream(found.Content, found.Metadata.ContentType, found.Metadata.FileName);

            case AttachmentDownloadResult.NotFound:
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

    private static async Task<IResult> UploadAsync(
        Guid pageId,
        HttpRequest request,
        IAttachmentService attachmentService,
        ICurrentPrincipalAccessor principalAccessor,
        IActingUserAccessor actingUserAccessor,
        ICurrentAuditContextAccessor auditContextAccessor,
        IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return Results.Json(unauthenticated, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Expected multipart/form-data with a single file field." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.Count > 0 ? form.Files[0] : null;
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "No file provided." });
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

    private static int MapErrorStatusCode(string kind) => kind switch
    {
        "Forbidden" => StatusCodes.Status403Forbidden,
        "ReadOnlyReplica" => StatusCodes.Status403Forbidden,
        "NotFound" => StatusCodes.Status404NotFound,
        "Validation" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
