using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IAccessRuleService. Lives in RocketWiki.Data for the
/// same reason PageService/PageReadService do: it needs RocketWikiDbContext directly.
///
/// <para>Three kinds, one CRUD surface (design.md §6.4/§8): role grants, access grants
/// (with their selector rows, §21.15) and page restrictions. The management gate is
/// role-only — <see cref="RuleManagementGate"/> over this space's ROLE grants, or instance
/// admin — so a space-admin who holds no access grant manages a space whose pages they
/// cannot read (§6.5.2); managing the rules that decide who sees a space is not the same
/// act as seeing it.</para>
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
        var shapeError = AccessRuleValidation.ValidateShape(
            request.Kind, request.SpaceId, request.PageId, request.Role, request.Action, request.SelectorValues);
        if (shapeError is not null)
        {
            return PageMutationResult<AccessRule>.Failure(shapeError);
        }

        if (!RuleExpressionSerializer.TryParse(request.ExpressionJson, out _, out var parseError))
        {
            return PageMutationResult<AccessRule>.Failure(new ValidationError($"Invalid rule expression: {parseError}"));
        }

        var selectorError = AccessRuleValidation.ValidateSelectors(_db.SelectorCatalog, request.SelectorValues, out var selectors);
        if (selectorError is not null)
        {
            return PageMutationResult<AccessRule>.Failure(selectorError);
        }

        var spaceLookup = await ResolveSpaceAsync(request.Kind, request.SpaceId, request.PageId, cancellationToken);
        if (spaceLookup is null)
        {
            return PageMutationResult<AccessRule>.Failure(new NotFoundError(request.PageId ?? request.SpaceId ?? Guid.Empty));
        }

        if (!await CanManageRulesAsync(spaceLookup.Id, principal, isInstanceAdmin, cancellationToken))
        {
            return PageMutationResult<AccessRule>.Failure(new ForbiddenError("instance admin or space admin required"));
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
        foreach (var selector in selectors)
        {
            rule.Selectors.Add(new AccessRuleSelector { AccessRuleId = rule.Id, Category = selector.Category, Value = selector.Value });
        }

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
        var rule = await _db.AccessRules
            .Include(r => r.Selectors)
            .FirstOrDefaultAsync(r => r.Id == request.AccessRuleId, cancellationToken);
        if (rule is null)
        {
            return PageMutationResult<AccessRule>.Failure(new NotFoundError(request.AccessRuleId));
        }

        var newRole = request.Role ?? rule.Role;
        var newAction = request.Action ?? rule.Action;
        var shapeError = AccessRuleValidation.ValidateShape(
            rule.Kind, rule.SpaceId, rule.PageId, newRole, newAction, request.SelectorValues);
        if (shapeError is not null)
        {
            return PageMutationResult<AccessRule>.Failure(shapeError);
        }

        if (!RuleExpressionSerializer.TryParse(request.ExpressionJson, out _, out var parseError))
        {
            return PageMutationResult<AccessRule>.Failure(new ValidationError($"Invalid rule expression: {parseError}"));
        }

        var selectorError = AccessRuleValidation.ValidateSelectors(_db.SelectorCatalog, request.SelectorValues, out var selectors);
        if (selectorError is not null)
        {
            return PageMutationResult<AccessRule>.Failure(selectorError);
        }

        var spaceLookup = await ResolveSpaceAsync(rule.Kind, rule.SpaceId, rule.PageId, cancellationToken);
        if (spaceLookup is null)
        {
            return PageMutationResult<AccessRule>.Failure(new NotFoundError(request.AccessRuleId));
        }

        if (!await CanManageRulesAsync(spaceLookup.Id, principal, isInstanceAdmin, cancellationToken))
        {
            return PageMutationResult<AccessRule>.Failure(new ForbiddenError("instance admin or space admin required"));
        }

        // Captured before any field is mutated - this IS the "before" state design.md
        // §7 requires, not an approximation of it.
        var before = rule.ToSnapshot();

        rule.ExpressionJson = request.ExpressionJson;
        rule.Role = newRole;
        rule.Action = newAction;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedByUserId = actingUserId;

        // Null means "leave the conferred selectors alone"; a list (empty included) is
        // the full replacement set, applied as a diff so an unchanged row is not a
        // pointless delete-and-insert pair inside the transaction.
        if (request.SelectorValues is not null)
        {
            ReplaceSelectors(rule, selectors);
        }

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AccessRuleChangedEvent(rule.Id, spaceLookup.Key, actingUserId, before, After: rule.ToSnapshot()));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<AccessRule>.Success(rule);
    }

    public async Task<PageMutationResult<Guid>> DeleteAsync(
        DeleteAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var rule = await _db.AccessRules
            .Include(r => r.Selectors)
            .FirstOrDefaultAsync(r => r.Id == request.AccessRuleId, cancellationToken);
        if (rule is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.AccessRuleId));
        }

        var spaceLookup = await ResolveSpaceAsync(rule.Kind, rule.SpaceId, rule.PageId, cancellationToken);
        if (spaceLookup is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.AccessRuleId));
        }

        if (!await CanManageRulesAsync(spaceLookup.Id, principal, isInstanceAdmin, cancellationToken))
        {
            return PageMutationResult<Guid>.Failure(new ForbiddenError("instance admin or space admin required"));
        }

        var before = rule.ToSnapshot();

        // NO ACTION on the FK (data-model.md), so the selector rows go explicitly, in the
        // same transaction - never as a side effect the database performed for us.
        _db.AccessRuleSelectors.RemoveRange(rule.Selectors);
        _db.AccessRules.Remove(rule);

        // design.md §7: deletion records After as explicitly null, so a replay can
        // distinguish "deleted" from "unchanged".
        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new AccessRuleChangedEvent(rule.Id, spaceLookup.Key, actingUserId, before, After: null));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Guid>.Success(rule.Id);
    }

    /// <summary>Diffs the grant's selector rows against the wanted (canonical) set — the
    /// same shape <c>PageMarkingService.ReplaceCountries</c> uses, for the same reason.</summary>
    private void ReplaceSelectors(AccessRule rule, IReadOnlyList<SelectorValue> wanted)
    {
        var wantedSet = wanted.ToHashSet();

        foreach (var existing in rule.Selectors.Where(s => !wantedSet.Contains(SelectorValue.Canonical(s.Category, s.Value))).ToList())
        {
            rule.Selectors.Remove(existing);
            _db.AccessRuleSelectors.Remove(existing);
        }

        var held = rule.SelectorValues();
        foreach (var selector in wanted.Where(s => !held.Contains(s)))
        {
            rule.Selectors.Add(new AccessRuleSelector { AccessRuleId = rule.Id, Category = selector.Category, Value = selector.Value });
        }
    }

    private async Task<Space?> ResolveSpaceAsync(AccessRuleKind kind, Guid? spaceId, Guid? pageId, CancellationToken cancellationToken)
    {
        if (kind is AccessRuleKind.RoleGrant or AccessRuleKind.AccessGrant)
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

    /// <summary>design.md §6.5.2's gate via <see cref="RuleManagementGate"/> — the one
    /// shared definition this service's mutations and the API's restriction/grant
    /// read paths all call, so the write gate and the read gate cannot drift. Loads ROLE
    /// grants only: an access grant confers no say over the rules.</summary>
    private async Task<bool> CanManageRulesAsync(Guid spaceId, Principal principal, bool isInstanceAdmin, CancellationToken cancellationToken)
    {
        var grants = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.RoleGrant && r.SpaceId == spaceId)
            .ToListAsync(cancellationToken);

        return RuleManagementGate.CanManageRules(
            EffectivePermissionCalculator.ComputeSpaceRole(grants, principal), isInstanceAdmin);
    }
}
