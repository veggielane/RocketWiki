using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of <see cref="IWatchService"/> — see the interface doc for
/// the authorization shape (canView to watch, nothing to unwatch, replicas watchable).
/// Lives in RocketWiki.Data like every other *Service: it needs RocketWikiDbContext.
/// </summary>
public class WatchService : IWatchService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;
    private readonly PermissionContextLoader _permissions;

    public WatchService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db);
    }

    public async Task<PageMutationResult<Watch>> WatchPageAsync(
        WatchPageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Watch>.Failure(new NotFoundError(request.PageId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Watch>.Failure(new NotFoundError(page.SpaceId));
        }

        // "You can't watch what you can't see" - same gate, and same Forbidden shape,
        // as commenting (CommentService): canView, evaluated against the token-built
        // Principal. Deliberately NOT a replica check - see IWatchService's doc.
        var canView = await ComputeCanViewAsync(space, page, principal, cancellationToken);
        if (!canView)
        {
            return PageMutationResult<Watch>.Failure(new ForbiddenError("canView required"));
        }

        var existing = await _db.Watches.FirstOrDefaultAsync(
            w => w.UserId == actingUserId && w.PageId == page.Id, cancellationToken);
        if (existing is not null)
        {
            return PageMutationResult<Watch>.Success(existing); // idempotent - nothing changed, nothing to audit
        }

        var watch = new Watch
        {
            UserId = actingUserId,
            PageId = page.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.Watches.Add(watch);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new WatchAddedEvent(watch.Id, page.Id, null, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Watch>.Success(watch);
    }

    public async Task<PageMutationResult<Watch>> UnwatchPageAsync(
        UnwatchPageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var watch = await _db.Watches.FirstOrDefaultAsync(
            w => w.UserId == actingUserId && w.PageId == request.PageId, cancellationToken);
        if (watch is null)
        {
            return PageMutationResult<Watch>.Failure(new NotFoundError(request.PageId));
        }

        // IgnoreQueryFilters: the watched page may have been soft-deleted (or its space
        // archived) since - unwatching must still work then, and the audit row still
        // wants the space key. FK integrity guarantees both rows exist.
        var spaceKey = await _db.Pages.IgnoreQueryFilters()
            .Where(p => p.Id == request.PageId)
            .Join(_db.Spaces.IgnoreQueryFilters(), p => p.SpaceId, s => s.Id, (_, s) => s.Key)
            .FirstAsync(cancellationToken);

        _db.Watches.Remove(watch);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new WatchRemovedEvent(watch.Id, request.PageId, null, spaceKey, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Watch>.Success(watch);
    }

    public async Task<PageMutationResult<Watch>> WatchSpaceAsync(
        WatchSpaceRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Watch>.Failure(new NotFoundError(request.SpaceId));
        }

        // A space's own view gate is space access (Query.Spaces, design.md §6.4) - the
        // same gate governs watching it.
        var grants = await _db.AccessRules
            .Include(r => r.Selectors)
            .Where(r => (r.Kind == AccessRuleKind.RoleGrant || r.Kind == AccessRuleKind.AccessGrant) && r.SpaceId == space.Id)
            .ToListAsync(cancellationToken);
        if (!EffectivePermissionCalculator.HasSpaceAccess(grants, principal))
        {
            return PageMutationResult<Watch>.Failure(new ForbiddenError(EffectivePermissionCalculator.NoSpaceAccessReason));
        }

        var existing = await _db.Watches.FirstOrDefaultAsync(
            w => w.UserId == actingUserId && w.SpaceId == space.Id, cancellationToken);
        if (existing is not null)
        {
            return PageMutationResult<Watch>.Success(existing); // idempotent
        }

        var watch = new Watch
        {
            UserId = actingUserId,
            SpaceId = space.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.Watches.Add(watch);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new WatchAddedEvent(watch.Id, null, space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Watch>.Success(watch);
    }

    public async Task<PageMutationResult<Watch>> UnwatchSpaceAsync(
        UnwatchSpaceRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var watch = await _db.Watches.FirstOrDefaultAsync(
            w => w.UserId == actingUserId && w.SpaceId == request.SpaceId, cancellationToken);
        if (watch is null)
        {
            return PageMutationResult<Watch>.Failure(new NotFoundError(request.SpaceId));
        }

        var spaceKey = await _db.Spaces.IgnoreQueryFilters()
            .Where(s => s.Id == request.SpaceId)
            .Select(s => s.Key)
            .FirstAsync(cancellationToken);

        _db.Watches.Remove(watch);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new WatchRemovedEvent(watch.Id, null, request.SpaceId, spaceKey, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Watch>.Success(watch);
    }

    private async Task<bool> ComputeCanViewAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(page, space.IsReplicaOf(_localInstanceId), cancellationToken);
        return context.Compute(principal).CanView;
    }
}
