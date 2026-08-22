using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Metadata-only half of <see cref="IAttachmentService"/> — upload is deliberately
/// NOT here. GraphQL responses aren't suited to binary streaming (design.md §8);
/// upload lives at the REST route (<c>POST /attachments/{pageId}</c>,
/// <c>Attachments/AttachmentEndpoints.cs</c>), which shares this same
/// MutationAuthHelper-adjacent pattern for its own denial auditing.
/// </summary>
public partial class Mutation
{
    [AuditAction("attachment.delete")]
    public async Task<DeleteAttachmentPayload> DeleteAttachment(
        DeleteAttachmentRequest input,
        [Service] IAttachmentService attachmentService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new DeleteAttachmentPayload(null, unauthenticated);
        }

        var result = await attachmentService.DeleteAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "attachment.delete", result.Error, AuditSubjectType.Attachment, input.AttachmentId, cancellationToken);
            return new DeleteAttachmentPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new DeleteAttachmentPayload(result.Value, null);
    }
}

public sealed record DeleteAttachmentPayload(Attachment? Attachment, PageMutationErrorView? Error);
