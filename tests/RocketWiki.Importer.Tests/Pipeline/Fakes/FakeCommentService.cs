using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Importer.Tests.Pipeline.Fakes;

/// <summary>Enforces the same rule the real service does that the importer's ordering depends on: a reply's parent comment must already exist on the same page.</summary>
public sealed class FakeCommentService : ICommentService
{
    private readonly Dictionary<Guid, Comment> _comments = [];

    public List<AddCommentRequest> AddCalls { get; } = [];

    public Func<AddCommentRequest, PageMutationError?>? FailAddWhen { get; set; }

    public Task<PageMutationResult<Comment>> AddCommentAsync(
        AddCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        AddCalls.Add(request);

        if (request.ParentCommentId is { } parentId
            && (!_comments.TryGetValue(parentId, out var parent) || parent.PageId != request.PageId))
        {
            return Task.FromResult(PageMutationResult<Comment>.Failure(new ValidationError("Parent comment must exist on the same page.")));
        }

        var failure = FailAddWhen?.Invoke(request);
        if (failure is not null)
        {
            return Task.FromResult(PageMutationResult<Comment>.Failure(failure));
        }

        var comment = new Comment
        {
            PageId = request.PageId,
            ParentCommentId = request.ParentCommentId,
            Body = request.Body,
            AuthorUserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _comments[comment.Id] = comment;
        return Task.FromResult(PageMutationResult<Comment>.Success(comment));
    }

    public Task<PageMutationResult<EditedComment>> EditCommentAsync(EditCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call EditCommentAsync.");

    public Task<PageMutationResult<Comment>> DeleteCommentAsync(DeleteCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ConfluenceSpaceImporter is not expected to call DeleteCommentAsync.");
}
