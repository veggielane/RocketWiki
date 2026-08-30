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
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance admin required"));
        }

        if (!RuleExpressionSerializer.TryParse(initialGrant.ExpressionJson, out _, out var parseError))
        {
            return PageMutationResult<Space>.Failure(new ValidationError($"Invalid initial grant expression: {parseError}"));
        }

        // URLs are case-insensitive, so a key is canonicalized (trimmed, upper-cased)
        // before anything else looks at it — see SpaceKeys.Canonical. Canonicalized here
        // rather than only at the persistence seam for the same reason PageService
        // canonicalizes a slug: the taken-check below compares against stored keys, which
        // are canonical, and under the BIN2 index a raw "eng" would not match a stored
        // "ENG" — the check would pass and the unique index would then turn a clean
        // ValidationError into a 500.
        var key = SpaceKeys.Canonical(request.Key);

        var keyTaken = await _db.Spaces.AnyAsync(s => s.Key == key, cancellationToken);
        if (keyTaken)
        {
            return PageMutationResult<Space>.Failure(new ValidationError($"Space key '{key}' is already in use."));
        }

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = key,
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
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance admin or space admin required"));
        }

        var oldName = space.Name;
        space.Name = request.Name;
        space.Description = request.Description;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceRenamedEvent(space.Id, space.Key, actingUserId, oldName, request.Name));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Space>.Success(space);
    }

    public async Task<PageMutationResult<Space>> SetHomepageAsync(
        SetSpaceHomepageRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Space>.Failure(new NotFoundError(request.SpaceId));
        }

        if (!isInstanceAdmin && !await IsSpaceAdminAsync(space.Id, principal, cancellationToken))
        {
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance admin or space admin required"));
        }

        // No ReadOnlyReplicaError arm, matching Rename/Archive/Restore rather than the
        // page services: design.md §12's table puts space lifecycle and identity in the
        // "stays local" column, so choosing a replica's own default page is local
        // curation, not a content write reaching back across the boundary.
        if (request.PageId is { } pageId)
        {
            // IgnoreQueryFilters so a trashed page is found rather than silently reading
            // as "no such page" - the two deserve different answers, and only this query
            // can tell them apart.
            var page = await _db.Pages.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);

            // "Missing" and "belongs to another space" collapse into one message on
            // purpose: distinguishing them would answer "does this page id exist?" for
            // pages in spaces this caller administers nothing in (design.md §6.7's
            // reasoning, applied to a mutation's refusal).
            if (page is null || page.SpaceId != space.Id)
            {
                return PageMutationResult<Space>.Failure(new ValidationError(
                    $"Page {pageId} is not a page in space '{space.Key}'."));
            }

            if (page.IsDeleted)
            {
                return PageMutationResult<Space>.Failure(new ValidationError(
                    $"Page {pageId} is in the trash and cannot be the homepage of space '{space.Key}'."));
            }
        }

        var oldPageId = space.HomepageId;
        space.HomepageId = request.PageId;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceHomepageSetEvent(space.Id, space.Key, actingUserId, oldPageId, request.PageId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Space>.Success(space);
    }

    public async Task<PageMutationResult<Space>> SetExportedAsync(
        SetSpaceExportedRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        if (!isInstanceAdmin)
        {
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance admin required"));
        }

        // IgnoreQueryFilters: an archived space can still legitimately be flagged (the
        // sync CLI already reasons this way when producing its baseline — exported-ness
        // and archival are independent properties).
        var space = await _db.Spaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<Space>.Failure(new NotFoundError(request.SpaceId));
        }

        // design.md §12: only a native space can be exported. Checked before the no-op
        // shortcut below so a replica gets the same answer whichever state it is in.
        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<Space>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        if (space.IsExported == request.Exported)
        {
            // Already in the requested state. Idempotent success, and deliberately no
            // event: an audit row saying export was "enabled" when it already was would
            // put a false widening in front of the reviewer §7 writes these rows for.
            return PageMutationResult<Space>.Success(space);
        }

        space.IsExported = request.Exported;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SpaceExportChangedEvent(space.Id, space.Key, actingUserId, request.Exported));

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
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance admin or space admin required"));
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
            return PageMutationResult<Space>.Failure(new ForbiddenError("instance admin or space admin required"));
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
