using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Configurations;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of ILabelService. Lives in RocketWiki.Data for the same
/// reason every other service here does: it needs RocketWikiDbContext directly.
/// </summary>
public class LabelService : ILabelService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;
    private readonly PermissionContextLoader _permissions;

    public LabelService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db);
    }

    public async Task<PageMutationResult<Label>> CreateLabelAsync(
        CreateLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Label>.Failure(new NotFoundError(request.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Label>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);
        var role = EffectivePermissionCalculator.ComputeSpaceRole(spaceGrants, principal);
        if (role is null || role.Value < SpaceRole.Editor)
        {
            return PageMutationResult<Label>.Failure(new ForbiddenError("editor role required"));
        }

        // Checked here rather than left to the column, for the same tier-parity reason
        // PageMarkingService checks its prefix length: SQLite does not enforce declared
        // string lengths, so an over-long name stores silently in the test tier and comes
        // back as a raw DbUpdateException in production. Confluence allows 255 against this
        // column's 100, so a Confluence import is exactly where it is met — and down the
        // importer's path an unhandled exception aborts the whole run rather than being
        // reported as the label failure it is.
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > LabelConfiguration.MaxNameLength)
        {
            return PageMutationResult<Label>.Failure(new ValidationError(
                $"A label name must be 1-{LabelConfiguration.MaxNameLength} characters."));
        }

        var nameTaken = await _db.Labels.AnyAsync(l => l.SpaceId == space.Id && l.Name == name, cancellationToken);
        if (nameTaken)
        {
            return PageMutationResult<Label>.Failure(new ValidationError($"Label '{name}' already exists in this space."));
        }

        var label = new Label { SpaceId = space.Id, Name = name };
        _db.Labels.Add(label);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new LabelCreatedEvent(label.Id, space.Id, space.Key, actingUserId, label.Name));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Label>.Success(label);
    }

    public async Task<PageMutationResult<PageLabel>> AttachLabelAsync(
        AttachLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<PageLabel>.Failure(new NotFoundError(request.PageId));
        }

        var label = await _db.Labels.FirstOrDefaultAsync(l => l.Id == request.LabelId, cancellationToken);
        if (label is null)
        {
            return PageMutationResult<PageLabel>.Failure(new NotFoundError(request.LabelId));
        }

        if (label.SpaceId != page.SpaceId)
        {
            return PageMutationResult<PageLabel>.Failure(new ValidationError("Label and page must belong to the same space."));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<PageLabel>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<PageLabel>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var canEdit = await ComputeCanEditAsync(space, page, principal, cancellationToken);
        if (!canEdit)
        {
            return PageMutationResult<PageLabel>.Failure(new ForbiddenError("canEdit required"));
        }

        var alreadyAttached = await _db.PageLabels.AnyAsync(pl => pl.PageId == page.Id && pl.LabelId == label.Id, cancellationToken);
        if (alreadyAttached)
        {
            return PageMutationResult<PageLabel>.Failure(new ValidationError("Label is already attached to this page."));
        }

        var pageLabel = new PageLabel { PageId = page.Id, LabelId = label.Id };
        _db.PageLabels.Add(pageLabel);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new LabelAttachedEvent(label.Id, page.Id, space.Id, space.Key, actingUserId, label.Name));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageLabel>.Success(pageLabel);
    }

    public async Task<PageMutationResult<Guid>> DetachLabelAsync(
        DetachLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var pageLabel = await _db.PageLabels.FirstOrDefaultAsync(
            pl => pl.PageId == request.PageId && pl.LabelId == request.LabelId, cancellationToken);
        if (pageLabel is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.LabelId));
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Guid>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var canEdit = await ComputeCanEditAsync(space, page, principal, cancellationToken);
        if (!canEdit)
        {
            return PageMutationResult<Guid>.Failure(new ForbiddenError("canEdit required"));
        }

        var label = await _db.Labels.FirstOrDefaultAsync(l => l.Id == request.LabelId, cancellationToken);
        var labelName = label?.Name ?? string.Empty;

        _db.PageLabels.Remove(pageLabel);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new LabelDetachedEvent(request.LabelId, page.Id, space.Id, space.Key, actingUserId, labelName));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Guid>.Success(request.LabelId);
    }

    public async Task<IReadOnlyList<Page>> GetPagesByLabelAsync(
        Guid spaceId, string labelName, Principal principal, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken);
        if (space is null)
        {
            return Array.Empty<Page>();
        }

        var spaceGrants = await _permissions.LoadSpaceGrantsAsync(spaceId, cancellationToken);
        if (EffectivePermissionCalculator.ComputeSpaceRole(spaceGrants, principal) is null)
        {
            return Array.Empty<Page>(); // no space role at all - nothing is visible (design.md §6.7)
        }

        var candidatePages = await _db.Pages
            .Where(p => p.SpaceId == spaceId && p.PageLabels.Any(pl => pl.Label!.Name == labelName))
            .ToListAsync(cancellationToken);

        if (candidatePages.Count == 0)
        {
            return Array.Empty<Page>();
        }

        // design.md §6.7: a label listing must not reveal a restricted page's existence
        // by any means, including by omission-implied count - each candidate is
        // filtered individually against its own ancestor chain, exactly like the page tree.
        // Batched: one restrictions query for the whole candidate set (the space's
        // grants are already in hand from the role gate above), then in-memory evaluation.
        var subjects = candidatePages.Select(PermissionSubject.For).ToList();
        var batch = await _permissions.LoadBatchAsync(subjects, spaceId, spaceGrants, cancellationToken);

        var visiblePages = new List<Page>();
        foreach (var page in candidatePages)
        {
            // Replica status is irrelevant to canView (design.md §6.4).
            if (batch.For(PermissionSubject.For(page), isReplicaSpace: false).Compute(principal).CanView)
            {
                visiblePages.Add(page);
            }
        }

        return visiblePages;
    }

    private async Task<bool> ComputeCanEditAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(page, space.IsReplicaOf(_localInstanceId), cancellationToken);
        return context.Compute(principal).CanEdit;
    }
}
