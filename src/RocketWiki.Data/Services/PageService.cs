using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Telemetry;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-Core-backed implementation of IPageService. Lives in RocketWiki.Data rather than
/// RocketWiki.Core (which design.md §14's repo layout lists "services" under) because it
/// needs RocketWikiDbContext directly, and Data already references Core for entities -
/// putting an EF-dependent service in Core would require a circular project reference.
/// Core keeps the interface, requests, results, and errors; this class is the only piece
/// that knows EF Core exists.
/// </summary>
public class PageService : IPageService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;

    public PageService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
    }

    public async Task<PageMutationResult<Page>> CreatePageAsync(
        CreatePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(request.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Page>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        Page? parent = null;
        if (request.ParentPageId is not null)
        {
            parent = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.ParentPageId, cancellationToken);
            if (parent is null)
            {
                return PageMutationResult<Page>.Failure(new NotFoundError(request.ParentPageId.Value));
            }
        }

        // The new page has no restrictions of its own yet, so it inherits exactly what
        // its prospective parent (and the parent's ancestors) already carries
        // (design.md §6.4: restrictions accumulate down the tree).
        var inheritedRestrictionIds = parent is null
            ? Array.Empty<Guid>()
            : parent.GetAncestorIds().Append(parent.Id).ToArray();

        var permission = await ComputeEffectivePermissionAsync(space, inheritedRestrictionIds, principal, cancellationToken);
        if (!permission.CanEdit)
        {
            return PageMutationResult<Page>.Failure(new ForbiddenError(permission.EditDenialReason ?? "forbidden"));
        }

        var siblingSlugTaken = await _db.Pages.AnyAsync(
            p => p.SpaceId == space.Id && p.ParentPageId == request.ParentPageId && p.Slug == request.Slug,
            cancellationToken);
        if (siblingSlugTaken)
        {
            return PageMutationResult<Page>.Failure(new ValidationError($"Slug '{request.Slug}' is already used by a sibling page."));
        }

        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id,
            ParentPageId = parent?.Id,
            AncestorPath = parent is null ? "/" : $"{parent.AncestorPath}{parent.Id}/",
            Slug = request.Slug,
            Title = request.Title,
            CurrentContent = request.Content,
            CurrentRevisionNumber = 1,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.Pages.Add(page);
        _db.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id,
            RevisionNumber = 1,
            Title = request.Title,
            Content = request.Content,
            AuthorUserId = actingUserId,
            CreatedAtUtc = now,
        });

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, actingUserId, page.Title));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Page>.Success(page);
    }

    public async Task<PageMutationResult<Page>> UpdatePageContentAsync(
        UpdatePageContentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Page>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var restrictionIds = page.GetAncestorIds().Append(page.Id).ToArray();
        var permission = await ComputeEffectivePermissionAsync(space, restrictionIds, principal, cancellationToken);
        if (!permission.CanEdit)
        {
            return PageMutationResult<Page>.Failure(new ForbiddenError(permission.EditDenialReason ?? "forbidden"));
        }

        // Optimistic concurrency (design.md §5/§8): the editor holds the revision number
        // it loaded; a newer revision existing means someone else saved first.
        if (request.ExpectedRevisionNumber != page.CurrentRevisionNumber)
        {
            return PageMutationResult<Page>.Failure(new StaleRevisionError(
                request.ExpectedRevisionNumber, page.CurrentRevisionNumber, page.Title, page.CurrentContent));
        }

        var now = DateTime.UtcNow;
        var newRevisionNumber = page.CurrentRevisionNumber + 1;
        _db.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id,
            RevisionNumber = newRevisionNumber,
            Title = request.Title,
            Content = request.Content,
            EditSummary = request.EditSummary,
            AuthorUserId = actingUserId,
            CreatedAtUtc = now,
        });

        page.Title = request.Title;
        page.CurrentContent = request.Content;
        page.CurrentRevisionNumber = newRevisionNumber;
        page.UpdatedAtUtc = now;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageContentUpdatedEvent(page.Id, space.Id, space.Key, actingUserId, newRevisionNumber));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Page>.Success(page);
    }

    // design.md §15: the four operations below each span several queries before a single
    // SaveChanges, so SQL Client's per-query spans alone can't show how long the whole
    // unit of work took or how many pages it touched. Simple single-query mutations
    // (create, update content) are left to SQL Client's own instrumentation rather than
    // wrapped here for symmetry's sake.
    public async Task<PageMutationResult<Page>> MovePageAsync(
        MovePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.MovePageSpan);
        activity?.SetTag(DataTelemetry.PageIdTag, request.PageId);
        return DataTelemetry.Finish(activity,
            await MovePageCoreAsync(request, principal, actingUserId, auditContext, cancellationToken));
    }

    private async Task<PageMutationResult<Page>> MovePageCoreAsync(
        MovePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Page>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        Page? newParent = null;
        if (request.NewParentPageId is not null)
        {
            if (request.NewParentPageId == page.Id)
            {
                return PageMutationResult<Page>.Failure(new ValidationError("A page cannot be its own parent."));
            }

            newParent = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.NewParentPageId, cancellationToken);
            if (newParent is null)
            {
                return PageMutationResult<Page>.Failure(new NotFoundError(request.NewParentPageId.Value));
            }

            if (newParent.SpaceId != space.Id)
            {
                return PageMutationResult<Page>.Failure(new ValidationError("Cannot move a page into a different space."));
            }

            if (newParent.AncestorPath.Contains($"/{page.Id}/"))
            {
                return PageMutationResult<Page>.Failure(new ValidationError("Cannot move a page into its own subtree."));
            }
        }

        // A move must be permitted at both ends: the mover needs canEdit under the
        // page's current restrictions, and under the restrictions it would inherit at
        // the destination - otherwise "move" would be a way to relocate a page across a
        // restriction boundary the mover doesn't actually have edit rights on.
        var oldRestrictionIds = page.GetAncestorIds().Append(page.Id).ToArray();
        var oldPermission = await ComputeEffectivePermissionAsync(space, oldRestrictionIds, principal, cancellationToken);
        if (!oldPermission.CanEdit)
        {
            return PageMutationResult<Page>.Failure(new ForbiddenError(oldPermission.EditDenialReason ?? "forbidden"));
        }

        var newAncestorIds = newParent is null ? Array.Empty<Guid>() : newParent.GetAncestorIds().Append(newParent.Id).ToArray();
        var newRestrictionIds = newAncestorIds.Append(page.Id).ToArray();
        var newPermission = await ComputeEffectivePermissionAsync(space, newRestrictionIds, principal, cancellationToken);
        if (!newPermission.CanEdit)
        {
            return PageMutationResult<Page>.Failure(new ForbiddenError(newPermission.EditDenialReason ?? "forbidden"));
        }

        var oldParentPageId = page.ParentPageId;
        var oldAncestorPath = page.AncestorPath;
        var newAncestorPath = newParent is null ? "/" : $"{newParent.AncestorPath}{newParent.Id}/";

        // data-model.md: "a page move rewrites the subtree's paths in the move
        // transaction" - load every descendant via a prefix match on the OLD path
        // (the whole reason AncestorPath exists: one indexed query, no recursive CTE).
        var subtreePrefix = $"{oldAncestorPath}{page.Id}/";
        var descendants = await _db.Pages
            .Where(p => p.AncestorPath.StartsWith(subtreePrefix))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        page.ParentPageId = newParent?.Id;
        page.AncestorPath = newAncestorPath;
        page.SortOrder = request.NewSortOrder;
        page.UpdatedAtUtc = now;

        var newSubtreePrefix = $"{newAncestorPath}{page.Id}/";
        foreach (var descendant in descendants)
        {
            descendant.AncestorPath = newSubtreePrefix + descendant.AncestorPath[subtreePrefix.Length..];
            descendant.UpdatedAtUtc = now;
        }

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageMovedEvent(page.Id, space.Id, space.Key, actingUserId, oldParentPageId, newParent?.Id));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Page>.Success(page);
    }

    public async Task<PageMutationResult<PageDeleteSummary>> DeletePageAsync(
        DeletePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.SubtreeDeleteSpan);
        activity?.SetTag(DataTelemetry.PageIdTag, request.PageId);
        var result = DataTelemetry.Finish(activity,
            await DeletePageCoreAsync(request, principal, actingUserId, auditContext, cancellationToken));
        if (result.IsSuccess)
        {
            activity?.SetTag(DataTelemetry.PageCountTag, result.Value.DeletedPageCount);
        }

        return result;
    }

    private async Task<PageMutationResult<PageDeleteSummary>> DeletePageCoreAsync(
        DeletePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<PageDeleteSummary>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<PageDeleteSummary>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<PageDeleteSummary>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        // design.md §6.4.1: cascades to the whole LIVE subtree. Pages already
        // soft-deleted independently before this operation are left exactly as they
        // are - the global query filter already excludes them from `descendants`, so
        // they're neither re-stamped nor counted.
        var subtreePrefix = $"{page.AncestorPath}{page.Id}/";
        var liveDescendants = await _db.Pages
            .Where(p => p.AncestorPath.StartsWith(subtreePrefix))
            .ToListAsync(cancellationToken);

        var subtreePages = new List<Page> { page };
        subtreePages.AddRange(liveDescendants);

        var blockedCount = await CountPagesFailingCanEditAsync(space, subtreePages, principal, cancellationToken);
        if (blockedCount > 0)
        {
            // design.md §6.4.1: refuse the whole operation, and report only how many -
            // never which - since a descendant's title may itself be restricted.
            return PageMutationResult<PageDeleteSummary>.Failure(new SubtreeOperationForbiddenError(blockedCount));
        }

        var now = DateTime.UtcNow;
        var batchId = Guid.NewGuid();
        foreach (var subtreePage in subtreePages)
        {
            subtreePage.IsDeleted = true;
            subtreePage.DeletedAtUtc = now;
            subtreePage.DeletedByUserId = actingUserId;
            subtreePage.DeleteBatchId = batchId;
        }

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageSubtreeDeletedEvent(page.Id, space.Id, space.Key, actingUserId, subtreePages.Select(p => p.Id).ToList()));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageDeleteSummary>.Success(new PageDeleteSummary(page.Id, subtreePages.Count, now));
    }

    public async Task<PageMutationResult<PageRestoreSummary>> RestorePageAsync(
        RestorePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.SubtreeRestoreSpan);
        activity?.SetTag(DataTelemetry.PageIdTag, request.PageId);
        var result = DataTelemetry.Finish(activity,
            await RestorePageCoreAsync(request, principal, actingUserId, auditContext, cancellationToken));
        if (result.IsSuccess)
        {
            activity?.SetTag(DataTelemetry.PageCountTag, result.Value.RestoredPageCount);
        }

        return result;
    }

    private async Task<PageMutationResult<PageRestoreSummary>> RestorePageCoreAsync(
        RestorePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        var page = await _db.Pages.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null || !page.IsDeleted || page.DeleteBatchId is null)
        {
            return PageMutationResult<PageRestoreSummary>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<PageRestoreSummary>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<PageRestoreSummary>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        // Restore exactly the set deleted together with this page (matched by
        // DeleteBatchId) - not every currently-deleted descendant, since some of those
        // may have been trashed independently, before or after this cascade, and this
        // operation must not resurrect them too. DeleteBatchId replaces an earlier
        // (DeletedAtUtc, DeletedByUserId) correlation, which would have wrongly merged
        // two independent deletes by the same actor landing in the same millisecond.
        var subtreePrefix = $"{page.AncestorPath}{page.Id}/";
        var cascadeDescendants = await _db.Pages.IgnoreQueryFilters()
            .Where(p => p.AncestorPath.StartsWith(subtreePrefix)
                && p.IsDeleted
                && p.DeleteBatchId == page.DeleteBatchId)
            .ToListAsync(cancellationToken);

        var subtreePages = new List<Page> { page };
        subtreePages.AddRange(cascadeDescendants);

        var blockedCount = await CountPagesFailingCanEditAsync(space, subtreePages, principal, cancellationToken);
        if (blockedCount > 0)
        {
            return PageMutationResult<PageRestoreSummary>.Failure(new SubtreeOperationForbiddenError(blockedCount));
        }

        foreach (var subtreePage in subtreePages)
        {
            subtreePage.IsDeleted = false;
            subtreePage.DeletedAtUtc = null;
            subtreePage.DeletedByUserId = null;
            subtreePage.DeleteBatchId = null;
        }

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageSubtreeRestoredEvent(page.Id, space.Id, space.Key, actingUserId, subtreePages.Select(p => p.Id).ToList()));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageRestoreSummary>.Success(new PageRestoreSummary(page.Id, subtreePages.Count));
    }

    /// <summary>
    /// design.md §6.4.1: every page in a structural operation's subtree needs its OWN
    /// canEdit check - a deeper descendant may carry restrictions the target page
    /// itself doesn't. Loads the space's grants and every potentially-relevant
    /// restriction (the target's own ancestors, shared by the whole subtree, plus every
    /// page within the subtree, each of which may restrict itself) in two queries
    /// total, then evaluates each page in memory - no per-page round trip regardless of
    /// subtree size.
    /// </summary>
    private async Task<int> CountPagesFailingCanEditAsync(
        Space space, IReadOnlyCollection<Page> subtreePages, Principal principal, CancellationToken cancellationToken)
    {
        var rootAncestorIds = subtreePages.Count == 0 ? Array.Empty<Guid>() : subtreePages.First().GetAncestorIds();
        var subtreePageIds = subtreePages.Select(p => p.Id);
        var relevantRestrictionPageIds = rootAncestorIds.Concat(subtreePageIds).Distinct().ToArray();

        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);

        var allRelevantRestrictions = relevantRestrictionPageIds.Length == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && relevantRestrictionPageIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        var blockedCount = 0;
        foreach (var subtreePage in subtreePages)
        {
            var applicablePageIds = new HashSet<Guid>(subtreePage.GetAncestorIds()) { subtreePage.Id };
            var applicableRestrictions = allRelevantRestrictions.Where(r => applicablePageIds.Contains(r.PageId!.Value)).ToList();
            var permission = EffectivePermissionCalculator.Compute(spaceGrants, applicableRestrictions, isReplicaSpace: false, principal);
            if (!permission.CanEdit)
            {
                blockedCount++;
            }
        }

        return blockedCount;
    }

    public async Task<PageMutationResult<Page>> RestoreRevisionAsync(
        RestoreRevisionRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.RestoreRevisionSpan);
        activity?.SetTag(DataTelemetry.PageIdTag, request.PageId);
        activity?.SetTag(DataTelemetry.RevisionNumberTag, request.RevisionNumberToRestore);
        return DataTelemetry.Finish(activity,
            await RestoreRevisionCoreAsync(request, principal, actingUserId, auditContext, cancellationToken));
    }

    private async Task<PageMutationResult<Page>> RestoreRevisionCoreAsync(
        RestoreRevisionRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Page>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var restrictionIds = page.GetAncestorIds().Append(page.Id).ToArray();
        var permission = await ComputeEffectivePermissionAsync(space, restrictionIds, principal, cancellationToken);
        if (!permission.CanEdit)
        {
            return PageMutationResult<Page>.Failure(new ForbiddenError(permission.EditDenialReason ?? "forbidden"));
        }

        if (request.ExpectedCurrentRevisionNumber != page.CurrentRevisionNumber)
        {
            return PageMutationResult<Page>.Failure(new StaleRevisionError(
                request.ExpectedCurrentRevisionNumber, page.CurrentRevisionNumber, page.Title, page.CurrentContent));
        }

        var target = await _db.PageRevisions.FirstOrDefaultAsync(
            r => r.PageId == page.Id && r.RevisionNumber == request.RevisionNumberToRestore, cancellationToken);
        if (target is null)
        {
            return PageMutationResult<Page>.Failure(new NotFoundError(request.PageId));
        }

        // PageRevision is immutable and append-only (data-model.md) - "restoring" adds a
        // new revision carrying the old content rather than rewriting history.
        var now = DateTime.UtcNow;
        var newRevisionNumber = page.CurrentRevisionNumber + 1;
        _db.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id,
            RevisionNumber = newRevisionNumber,
            Title = target.Title,
            Content = target.Content,
            EditSummary = $"Restored from revision {target.RevisionNumber}",
            AuthorUserId = actingUserId,
            CreatedAtUtc = now,
        });

        page.Title = target.Title;
        page.CurrentContent = target.Content;
        page.CurrentRevisionNumber = newRevisionNumber;
        page.UpdatedAtUtc = now;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageRevisionRestoredEvent(page.Id, space.Id, space.Key, actingUserId, target.RevisionNumber));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Page>.Success(page);
    }

    private async Task<EffectivePermission> ComputeEffectivePermissionAsync(
        Space space, IReadOnlyCollection<Guid> restrictionPageIds, Principal principal, CancellationToken cancellationToken)
    {
        var spaceGrants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);

        var restrictions = restrictionPageIds.Count == 0
            ? new List<AccessRule>()
            : await _db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.PageRestriction && r.PageId != null && restrictionPageIds.Contains(r.PageId.Value))
                .ToListAsync(cancellationToken);

        return EffectivePermissionCalculator.Compute(spaceGrants, restrictions, space.IsReplicaOf(_localInstanceId), principal);
    }
}
