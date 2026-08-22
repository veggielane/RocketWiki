using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IAccessRuleService. Lives in RocketWiki.Data for the
/// same reason PageService/PageReadService do: it needs RocketWikiDbContext directly.
/// </summary>
public class AccessRuleService : IAccessRuleService
{
    private readonly RocketWikiDbContext _db;

    public AccessRuleService(RocketWikiDbContext db)
    {
        _db = db;
    }

    public async Task<PageMutationResult<AccessRule>> CreateAsync(
        CreateAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var shapeError = ValidateShape(request.Kind, request.SpaceId, request.PageId, request.Role, request.Action);
        if (shapeError is not null)
        {
            return PageMutationResult<AccessRule>.Failure(shapeError);
        }

        if (!RuleExpressionSerializer.TryParse(request.ExpressionJson, out _, out var parseError))
        {
            return PageMutationResult<AccessRule>.Failure(new ValidationError($"Invalid rule expression: {parseError}"));
        }

        var spaceLookup = await ResolveSpaceAsync(request.Kind, request.SpaceId, request.PageId, cancellationToken);
        if (spaceLookup is null)
        {
            return PageMutationResult<AccessRule>.Failure(new NotFoundError(request.PageId ?? request.SpaceId ?? Guid.Empty));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(spaceLookup.Id, principal, cancellationToken))
        {
            return PageMutationResult<AccessRule>.Failure(new ForbiddenError("instance-admin or space-admin required"));
        }

        var now = DateTime.UtcNow;
        var rule = new AccessRule
        {
            Kind = request.Kind,
            SpaceId = request.SpaceId,
            PageId = request.PageId,
            Role = request.Role,
            Action = request.Action,
            ExpressionJson = request.ExpressionJson,
            CreatedAtUtc = now,
            CreatedByUserId = actingUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actingUserId,
        };
        _db.AccessRules.Add(rule);

        // design.md §7: creation records Before as explicitly null, so a replay can
        // distinguish "created" from "unchanged".
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AccessRuleChangedEvent(rule.Id, spaceLookup.Key, actingUserId, Before: null, After: rule.ToSnapshot()));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<AccessRule>.Success(rule);
    }

    public async Task<PageMutationResult<AccessRule>> UpdateAsync(
        UpdateAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var rule = await _db.AccessRules.FirstOrDefaultAsync(r => r.Id == request.AccessRuleId, cancellationToken);
        if (rule is null)
        {
            return PageMutationResult<AccessRule>.Failure(new NotFoundError(request.AccessRuleId));
        }

        var newRole = request.Role ?? rule.Role;
        var newAction = request.Action ?? rule.Action;
        var shapeError = ValidateShape(rule.Kind, rule.SpaceId, rule.PageId, newRole, newAction);
        if (shapeError is not null)
        {
            return PageMutationResult<AccessRule>.Failure(shapeError);
        }

        if (!RuleExpressionSerializer.TryParse(request.ExpressionJson, out _, out var parseError))
        {
            return PageMutationResult<AccessRule>.Failure(new ValidationError($"Invalid rule expression: {parseError}"));
        }

        var spaceLookup = await ResolveSpaceAsync(rule.Kind, rule.SpaceId, rule.PageId, cancellationToken);
        if (spaceLookup is null)
        {
            return PageMutationResult<AccessRule>.Failure(new NotFoundError(request.AccessRuleId));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(spaceLookup.Id, principal, cancellationToken))
        {
            return PageMutationResult<AccessRule>.Failure(new ForbiddenError("instance-admin or space-admin required"));
        }

        // Captured before any field is mutated - this IS the "before" state design.md
        // §7 requires, not an approximation of it.
        var before = rule.ToSnapshot();

        rule.ExpressionJson = request.ExpressionJson;
        rule.Role = newRole;
        rule.Action = newAction;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedByUserId = actingUserId;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AccessRuleChangedEvent(rule.Id, spaceLookup.Key, actingUserId, before, After: rule.ToSnapshot()));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<AccessRule>.Success(rule);
    }

    public async Task<PageMutationResult<Guid>> DeleteAsync(
        DeleteAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var rule = await _db.AccessRules.FirstOrDefaultAsync(r => r.Id == request.AccessRuleId, cancellationToken);
        if (rule is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.AccessRuleId));
        }

        var spaceLookup = await ResolveSpaceAsync(rule.Kind, rule.SpaceId, rule.PageId, cancellationToken);
        if (spaceLookup is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.AccessRuleId));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(spaceLookup.Id, principal, cancellationToken))
        {
            return PageMutationResult<Guid>.Failure(new ForbiddenError("instance-admin or space-admin required"));
        }

        var before = rule.ToSnapshot();
        _db.AccessRules.Remove(rule);

        // design.md §7: deletion records After as explicitly null, so a replay can
        // distinguish "deleted" from "unchanged".
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AccessRuleChangedEvent(rule.Id, spaceLookup.Key, actingUserId, before, After: null));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Guid>.Success(rule.Id);
    }

    private static ValidationError? ValidateShape(AccessRuleKind kind, Guid? spaceId, Guid? pageId, SpaceRole? role, PageAction? action)
    {
        // Mirrors CK_AccessRules_KindColumnPairing (data-model.md) so a bad request is
        // rejected here rather than surfacing as an opaque DbUpdateException.
        if (kind == AccessRuleKind.SpaceGrant)
        {
            if (spaceId is null || pageId is not null || role is null || action is not null)
            {
                return new ValidationError("A SpaceGrant rule must set SpaceId and Role, and must not set PageId or Action.");
            }
        }
        else if (kind == AccessRuleKind.PageRestriction)
        {
            if (pageId is null || spaceId is not null || action is null || role is not null)
            {
                return new ValidationError("A PageRestriction rule must set PageId and Action, and must not set SpaceId or Role.");
            }
        }
        else
        {
            return new ValidationError($"Unknown AccessRuleKind: {kind}.");
        }

        return null;
    }

    private async Task<Space?> ResolveSpaceAsync(AccessRuleKind kind, Guid? spaceId, Guid? pageId, CancellationToken cancellationToken)
    {
        if (kind == AccessRuleKind.SpaceGrant)
        {
            return spaceId is null ? null : await _db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken);
        }

        if (pageId is null)
        {
            return null;
        }

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        return page is null ? null : await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
    }

    private async Task<bool> IsSpaceAdminAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken)
    {
        var grants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId == spaceId)
            .ToListAsync(cancellationToken);

        return EffectivePermissionCalculator.ComputeSpaceRole(grants, principal) == SpaceRole.SpaceAdmin;
    }
}
