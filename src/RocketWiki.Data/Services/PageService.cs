using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;
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
    private readonly PermissionContextLoader _permissions;

    public PageService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db);
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

        // A slug that a route would swallow produces a page that is created and then
        // permanently unreachable, so it is refused rather than accepted. Checked here,
        // on the server, because a client that has not been updated must not be able to
        // skip it.
        if (PageSlugs.IsReserved(request.Slug))
        {
            return PageMutationResult<Page>.Failure(new ValidationError(
                $"Slug '{request.Slug}' is reserved — a page cannot use it, because /spaces/{{key}}/{request.Slug} already addresses something else."));
        }

        // Unique per SPACE, not per parent: the slug is the page's address
        // (/spaces/{key}/{slug}) and the hierarchy is deliberately absent from it, so
        // that moving a page never changes its URL. Two pages under different parents
        // sharing a slug would make that address ambiguous.
        var slugTaken = await _db.Pages.AnyAsync(
            p => p.SpaceId == space.Id && !p.IsDeleted && p.Slug == request.Slug,
            cancellationToken);
        if (slugTaken)
        {
            return PageMutationResult<Page>.Failure(new ValidationError($"Slug '{request.Slug}' is already used by another page in this space."));
        }

        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id,
            ParentPageId = parent?.Id,
            AncestorPath = parent is null ? "/" : $"{parent.AncestorPath}{parent.Id}/",
            Slug = request.Slug,
            Title = request.Title,
            Icon = request.Icon,
            CurrentContent = request.Content,
            CurrentRevisionNumber = 1,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.Pages.Add(page);

        // design.md §21: every page is marked, from the moment it exists. A child
        // inherits its parent's marking - inheritance happens ONCE, here, producing a
        // value the page then owns; it is not re-derived from ancestors at read time, so
        // an editor can later raise or lower it without fighting the tree. A root page
        // starts at OFFICIAL. The permission computation above already used this same
        // marking (the loader reads the last chain element's, which is the parent's for a
        // create), so the caller has necessarily been cleared for what the new page
        // inherits - creating a page you could not then read is impossible by
        // construction rather than by a second check.
        //
        // No audit row of its own: the marking is part of what page.create created, and a
        // page.marking.set row alongside every page.create would be noise that made real
        // marking changes harder to find. The inherited value is deterministic from the
        // parent, which the page.create row already identifies.
        var inherited = parent is null
            ? ProtectiveMarking.Baseline
            : await _permissions.LoadMarkingAsync(parent.Id, cancellationToken);
        var marking = new PageMarking
        {
            PageId = page.Id,
            Level = inherited.Level,
            // The national prefix inherits exactly like the level and the caveat - it is
            // part of how this page's marking reads, and a child that rendered
            // differently from its parent for no reason would be the confusing outcome.
            // A root page takes ProtectiveMarking.Baseline's UK (design.md §21.12).
            Prefix = inherited.Prefix,
            SetAtUtc = now,
            SetByUserId = actingUserId,
        };
        foreach (var country in inherited.EyesOnly)
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = country });
        }

        _db.PageMarkings.Add(marking);
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
        UpdatePageContentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        IReadOnlyCollection<Guid>? sessionContributorUserIds = null, CancellationToken cancellationToken = default)
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

        var permission = await ComputeEffectivePermissionAsync(space, page, principal, cancellationToken);
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
        var revision = new PageRevision
        {
            PageId = page.Id,
            RevisionNumber = newRevisionNumber,
            Title = request.Title,
            Content = request.Content,
            EditSummary = request.EditSummary,
            AuthorUserId = actingUserId,
            CreatedAtUtc = now,
        };
        _db.PageRevisions.Add(revision);

        // design.md §8 co-editing: contributor attribution rows commit in the SAME
        // transaction as the revision they describe - like the audit row, a revision
        // and its attribution cannot exist without each other. The ids arrive only
        // from the server's edit-session registry (see the interface doc); dedup here
        // is defense in depth, not an invitation to pass duplicates.
        var contributorIds = sessionContributorUserIds?.Distinct().ToArray() ?? [];
        foreach (var contributorUserId in contributorIds)
        {
            _db.PageRevisionContributors.Add(new PageRevisionContributor
            {
                PageRevisionId = revision.Id,
                UserId = contributorUserId,
            });
        }

        page.Title = request.Title;
        // Set unconditionally, including to null: the request carries the icon the page
        // should HAVE after this save, so "cleared it" and "left it alone" have to be
        // the same code path. Treating null as "no change" would make removing an icon
        // impossible through the only mutation that can set one.
        page.Icon = request.Icon;
        page.CurrentContent = request.Content;
        page.CurrentRevisionNumber = newRevisionNumber;
        page.UpdatedAtUtc = now;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PageContentUpdatedEvent(
            page.Id, space.Id, space.Key, actingUserId, newRevisionNumber,
            contributorIds.Length > 0 ? contributorIds : null));

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
        var oldPermission = await ComputeEffectivePermissionAsync(space, page, principal, cancellationToken);
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

        // A trashed page's address goes back into the pool - the unique index is
        // filtered on IsDeleted, deliberately, so the slug of something in the bin
        // doesn't reserve a URL nobody can reach. The cost is this: somebody may have
        // taken it since, and then restoring would put two live pages at one address.
        // Caught here, as a refusal naming the slug, because the alternative is the
        // index catching it at SaveChanges and surfacing as an unhandled DbUpdateException
        // - a 500 for a situation the person restoring can actually resolve.
        var subtreeSlugs = subtreePages.Select(p => p.Slug).ToList();
        var takenSlug = await _db.Pages
            .Where(p => p.SpaceId == space.Id && !p.IsDeleted && subtreeSlugs.Contains(p.Slug))
            .Select(p => p.Slug)
            .FirstOrDefaultAsync(cancellationToken);
        if (takenSlug is not null)
        {
            return PageMutationResult<PageRestoreSummary>.Failure(new ValidationError(
                $"Slug '{takenSlug}' was taken by another page while this one was in the trash. " +
                "Rename that page, then restore this one."));
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
        var subjects = subtreePages.Select(PermissionSubject.For).ToList();
        var batch = await _permissions.LoadBatchAsync(subjects, cancellationToken);

        // The space's real replica flag, like every other canEdit computation: both
        // callers refuse a replica with ReadOnlyReplicaError before reaching here, so
        // this is a guard held rather than a condition relied on (design.md §6.4/§12).
        var isReplicaSpace = space.IsReplicaOf(_localInstanceId);

        var blockedCount = 0;
        foreach (var subject in subjects)
        {
            if (!batch.For(subject, isReplicaSpace).Compute(principal).CanEdit)
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

        var permission = await ComputeEffectivePermissionAsync(space, page, principal, cancellationToken);
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
        Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(page, space.IsReplicaOf(_localInstanceId), cancellationToken);
        return context.Compute(principal);
    }

    /// <summary>
    /// <paramref name="restrictionPageIds"/> is a restriction chain: root-most ancestor
    /// first, the page acted on last. For operations that land under a chain which is
    /// not the page's own - a create under a parent, a move's destination.
    /// </summary>
    private async Task<EffectivePermission> ComputeEffectivePermissionAsync(
        Space space, IReadOnlyList<Guid> restrictionPageIds, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(
            space.Id, restrictionPageIds, space.IsReplicaOf(_localInstanceId), cancellationToken);
        return context.Compute(principal);
    }
}
