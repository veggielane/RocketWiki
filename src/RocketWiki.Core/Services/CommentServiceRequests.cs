namespace RocketWiki.Core.Services;

public sealed record AddCommentRequest(Guid PageId, Guid? ParentCommentId, string Body);

public sealed record EditCommentRequest(Guid CommentId, string Body);

public sealed record DeleteCommentRequest(Guid CommentId);
