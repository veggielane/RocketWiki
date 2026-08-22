using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of ICommentService. Lives in RocketWiki.Data for the same
/// reason every other service here does: it needs RocketWikiDbContext directly.
/// </summary>
public class CommentService : ICommentService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;

    public CommentService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
    }

    public async Task<PageMutationResult<Comment>> AddCommentAsync(
        AddCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Comment>.Failure(new ReadOnlyReplicaError(space.Id));
        }

        // design.md §6.4: "comment = requires canView" - not canEdit. A viewer may comment.
        var canView = await ComputeCanViewAsync(space, page, principal, cancellationToken);
        if (!canView)
        {
            return PageMutationResult<Comment>.Failure(new ForbiddenError("canView required"));
        }

        if (request.ParentCommentId is not null)
        {
            var parentComment = await _db.Comments.FirstOrDefaultAsync(c => c.Id == request.ParentCommentId, cancellationToken);
            if (parentComment is null || parentComment.PageId != request.PageId)
            {
                return PageMutationResult<Comment>.Failure(new ValidationError("Parent comment must exist on the same page."));
            }
        }

        var comment = new Comment
        {
            PageId = page.Id,
            ParentCommentId = request.ParentCommentId,
            Body = request.Body,
            AuthorUserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.Comments.Add(comment);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new CommentAddedEvent(comment.Id, page.Id, space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Comment>.Success(comment);
    }

    public async Task<PageMutationResult<Comment>> EditCommentAsync(
        EditCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var comment = await _db.Comments.FirstOrDefaultAsync(c => c.Id == request.CommentId, cancellationToken);
        if (comment is null || comment.IsDeleted)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(request.CommentId));
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == comment.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(comment.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Comment>.Failure(new ReadOnlyReplicaError(space.Id));
        }

        // Judgement call: only the original author may edit their own comment - no
        // moderation role is specified anywhere for edits (unlike delete, below).
        if (comment.AuthorUserId != actingUserId)
        {
            return PageMutationResult<Comment>.Failure(new ForbiddenError("only the comment's author may edit it"));
        }

        comment.Body = request.Body;
        comment.EditedAtUtc = DateTime.UtcNow;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new CommentEditedEvent(comment.Id, page.Id, space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Comment>.Success(comment);
    }

    public async Task<PageMutationResult<Comment>> DeleteCommentAsync(
        DeleteCommentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var comment = await _db.Comments.FirstOrDefaultAsync(c => c.Id == request.CommentId, cancellationToken);
        if (comment is null || comment.IsDeleted)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(request.CommentId));
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == comment.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(comment.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Comment>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Comment>.Failure(new ReadOnlyReplicaError(space.Id));
        }

        // Judgement call: the author can always delete their own comment; someone with
        // canEdit on the page (basic thread moderation) can also remove it.
        var isAuthor = comment.AuthorUserId == actingUserId;
        if (!isAuthor)
        {
            var canEdit = await ComputeCanEditAsync(space, page, principal, cancellationToken);
            if (!canEdit)
            {
                return PageMutationResult<Comment>.Failure(new ForbiddenError("only the author or a page editor may delete this comment"));
            }
        }

        // data-model.md: tombstone, not a hard delete - keeps thread shape, body blanked.
        comment.IsDeleted = true;
        comment.Body = string.Empty;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new CommentDeletedEvent(comment.Id, page.Id, space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Comment>.Success(comment);
    }

    private async Task<bool> ComputeCanViewAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var permission = await ComputeEffectivePermissionAsync(space, page, principal, cancellationToken);
        return permission.CanView;
    }

    private async Task<bool> ComputeCanEditAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var permission = await ComputeEffectivePermissionAsync(space, page, principal, cancellationToken);
        return permission.CanEdit;
    }

    private async Task<EffectivePermission> ComputeEffectivePermissionAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);

        var restrictionIds = page.GetAncestorIds().Append(page.Id).ToArray();
        var restrictions = restrictionIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && restrictionIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        return EffectivePermissionCalculator.Compute(spaceGrants, restrictions, space.IsReplicaOf(_localInstanceId), principal);
    }
}
