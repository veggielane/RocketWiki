namespace RocketWiki.Core.Services;

public sealed record AddCommentRequest(Guid PageId, Guid? ParentCommentId, string Body);

public sealed record EditCommentRequest(Guid CommentId, string Body);

/// <summary>
/// A successful edit's result: the updated comment plus the body it replaced. Comments
/// keep no revision history (unlike pages, whose previous content the dispatcher can
/// re-read from PageRevisions), so the pre-edit body must travel with the result — it
/// is what design.md §8's delta rule diffs against so an edit notifies only NEWLY
/// mentioned users. Notification plumbing only; never persisted, never returned to
/// clients.
/// </summary>
public sealed record EditedComment(Entities.Comment Comment, string PreviousBody);

public sealed record DeleteCommentRequest(Guid CommentId);
