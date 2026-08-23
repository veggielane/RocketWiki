using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>Thin GraphQL layer over <see cref="ICommentService"/> - see Mutation.cs's
/// class doc for the shared plumbing (MutationAuthHelper) and why success/denial
/// auditing splits the way it does.</summary>
public partial class Mutation
{
    [AuditAction("comment.add")]
    public async Task<AddCommentPayload> AddComment(
        AddCommentRequest input,
        [Service] ICommentService commentService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] INotificationDispatcher notificationDispatcher,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new AddCommentPayload(null, unauthenticated);
        }

        var result = await commentService.AddCommentAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "comment.add", result.Error, AuditSubjectType.Comment, subjectId: null, cancellationToken);
            return new AddCommentPayload(null, PageMutationErrorView.From(result.Error));
        }

        // design.md §8/§4: mentions in a comment body notify at save, per-recipient
        // canView at send time (see NotificationDispatcher).
        await notificationDispatcher.NotifyCommentPostedAsync(result.Value.Id, actingUserId.Value, cancellationToken);

        return new AddCommentPayload(result.Value, null);
    }

    [AuditAction("comment.edit")]
    public async Task<EditCommentPayload> EditComment(
        EditCommentRequest input,
        [Service] ICommentService commentService,
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
            return new EditCommentPayload(null, unauthenticated);
        }

        var result = await commentService.EditCommentAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "comment.edit", result.Error, AuditSubjectType.Comment, input.CommentId, cancellationToken);
            return new EditCommentPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new EditCommentPayload(result.Value, null);
    }

    [AuditAction("comment.delete")]
    public async Task<DeleteCommentPayload> DeleteComment(
        DeleteCommentRequest input,
        [Service] ICommentService commentService,
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
            return new DeleteCommentPayload(null, unauthenticated);
        }

        var result = await commentService.DeleteCommentAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "comment.delete", result.Error, AuditSubjectType.Comment, input.CommentId, cancellationToken);
            return new DeleteCommentPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new DeleteCommentPayload(result.Value, null);
    }
}

public sealed record AddCommentPayload(Comment? Comment, PageMutationErrorView? Error);
public sealed record EditCommentPayload(Comment? Comment, PageMutationErrorView? Error);
public sealed record DeleteCommentPayload(Comment? Comment, PageMutationErrorView? Error);
