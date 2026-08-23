using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.4: "comment = requires canView" - deliberately NOT canEdit. A viewer,
/// not just an editor, may comment; adding a comment is not a content edit. Every
/// method is blocked on replicas (§12), same as every other mutation.
///
/// Edit is restricted to the comment's own author - not stated explicitly in the task,
/// but the natural reading of a comment system with no separate moderation role
/// mentioned anywhere. Delete additionally allows anyone with canEdit on the page
/// (basic moderation of a page's own comment thread) alongside the author - flagging
/// both as judgement calls.
///
/// Delete is a tombstone (data-model.md): IsDeleted set, Body blanked, the row and its
/// position in the thread retained so replies keep their parent - never a hard delete.
/// </summary>
public interface ICommentService
{
    Task<PageMutationResult<Comment>> AddCommentAsync(
        AddCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>Result carries the pre-edit body alongside the comment — see <see cref="EditedComment"/>.</summary>
    Task<PageMutationResult<EditedComment>> EditCommentAsync(
        EditCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Comment>> DeleteCommentAsync(
        DeleteCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
