using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of ISpaceService. Lives in RocketWiki.Data for the same
/// reason every other service here does: it needs RocketWikiDbContext directly.
/// </summary>
public class SpaceService : ISpaceService
{
    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;

    public SpaceService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
    }

    public async Task<PageMutationResult<Space>> CreateAsync(
        CreateSpaceRequest request, InitialSpaceGrant initialGrant, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        if (!isInstanceAdmin)
        {
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance-admin required"));
        }

        if (!RuleExpressionSerializer.TryParse(initialGrant.ExpressionJson, out _, out var parseError))
        {
            return PageMutationResult<Space>.Failure(new ValidationError($"Invalid initial grant expression: {parseError}"));
        }

        var keyTaken = await _db.Spaces.AnyAsync(s => s.Key == request.Key, cancellationToken);
        if (keyTaken)
        {
            return PageMutationResult<Space>.Failure(new ValidationError($"Space key '{request.Key}' is already in use."));
        }

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = request.Key,
            Name = request.Name,
            Description = request.Description,
            OriginInstanceId = _localInstanceId, // native space - created here, not a replica
            IsExported = false,
            LastOutboxSequence = 0,
            CreatedAtUtc = now,
            CreatedByUserId = actingUserId,
        };
        _db.Spaces.Add(space);

        // design.md §6.5.1: committed in the SAME transaction as the Space row (one
        // SaveChangesAsync below) - a space never exists, even momentarily, in a state
        // nobody can administer. Built directly against the in-memory `space` rather
        // than through IAccessRuleService, which would need to re-query it - `space`
        // isn't persisted yet, so that query would find nothing.
        var grant = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = initialGrant.Role,
            ExpressionJson = initialGrant.ExpressionJson,
            CreatedAtUtc = now,
            CreatedByUserId = actingUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actingUserId,
        };
        _db.AccessRules.Add(grant);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceCreatedEvent(space.Id, space.Key, actingUserId));
        // design.md §7: every AccessRule change is audited with full before/after state,
        // including this one - Before is explicitly null, exactly like a rule created
        // through IAccessRuleService.CreateAsync, so a replay sees the same shape either way.
        _db.RaiseDomainEvent(new AccessRuleChangedEvent(grant.Id, space.Key, actingUserId, Before: null, After: grant.ToSnapshot()));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Space>.Success(space);
    }

    public async Task<PageMutationResult<Space>> RenameAsync(
        RenameSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Space>.Failure(new NotFoundError(request.SpaceId));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(space.Id, principal, cancellationToken))
        {
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance-admin or space-admin required"));
        }

        var oldName = space.Name;
        space.Name = request.Name;
        space.Description = request.Description;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceRenamedEvent(space.Id, space.Key, actingUserId, oldName, request.Name));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Space>.Success(space);
    }

    public async Task<PageMutationResult<Space>> ArchiveAsync(
        ArchiveSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Space>.Failure(new NotFoundError(request.SpaceId));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(space.Id, principal, cancellationToken))
        {
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance-admin or space-admin required"));
        }

        // design.md §6.5.1: archives the Space row only - does not cascade to its
        // pages. Unlike page delete, archive removes neither content nor anyone's
        // access to it once restored, so it needs no per-page canEdit check.
        space.IsDeleted = true;
        space.DeletedAtUtc = DateTime.UtcNow;
        space.DeletedByUserId = actingUserId;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceArchivedEvent(space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Space>.Success(space);
    }

    public async Task<PageMutationResult<Space>> RestoreAsync(
        RestoreSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null || !space.IsDeleted)
        {
            return PageMutationResult<Space>.Failure(new NotFoundError(request.SpaceId));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(space.Id, principal, cancellationToken))
        {
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance-admin or space-admin required"));
        }

        space.IsDeleted = false;
        space.DeletedAtUtc = null;
        space.DeletedByUserId = null;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceRestoredEvent(space.Id, space.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Space>.Success(space);
    }

    private async Task<bool> IsSpaceAdminAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken)
    {
        var grants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == spaceId)
            .ToListAsync(cancellationToken);

        return EffectivePermissionCalculator.ComputeSpaceRole(grants, principal) == SpaceRole.SpaceAdmin;
    }
}
