using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IPagePropertyService"/> (design.md §20). Lives in
/// RocketWiki.Data for the same reason every other service here does: it needs
/// RocketWikiDbContext directly.
///
/// The value mutations take <c>localInstanceId</c> because they are page mutations and
/// therefore sit beneath the replica invariant (§12); the registry mutations do not use
/// it at all — the registry is instance-local, has no space, and so has no replica
/// question, exactly like CustomEmojiService.
/// </summary>
public class PagePropertyService : IPagePropertyService
{
    /// <summary>Matches PagePropertyConfiguration's HasMaxLength. Checked here rather
    /// than left to the column because SQLite does not enforce declared lengths (see
    /// SqliteTestBase's own note): without an application check, the same over-long
    /// value would be silently stored in the test tier and rejected by SQL Server in
    /// production — the exact tier disagreement PagePropertyKey.Normalize exists to
    /// prevent for uniqueness.</summary>
    public const int MaxValueLength = 1000;

    public const int MaxKeyLength = 64;

    public const int MaxDescriptionLength = 256;

    private readonly RocketWikiDbContext _db;
    private readonly string _localInstanceId;
    private readonly PermissionContextLoader _permissions;

    public PagePropertyService(RocketWikiDbContext db, string localInstanceId)
    {
        _db = db;
        _localInstanceId = localInstanceId;
        _permissions = new PermissionContextLoader(db);
    }

    public async Task<PageMutationResult<PagePropertyValue>> SetAsync(
        SetPagePropertyRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<PagePropertyValue>.Failure(new NotFoundError(request.PageId));
        }

        var key = await _db.PagePropertyKeys.FirstOrDefaultAsync(k => k.Id == request.PagePropertyKeyId, cancellationToken);
        if (key is null)
        {
            return PageMutationResult<PagePropertyValue>.Failure(new NotFoundError(request.PagePropertyKeyId));
        }

        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return PageMutationResult<PagePropertyValue>.Failure(new NotFoundError(page.SpaceId));
        }

        // Replica check before the permission check, like every other mutation service:
        // a replica refuses beneath every grant (design.md §12), so the answer must not
        // depend on whether this caller happened to hold canEdit.
        if (space.IsReplicaOf(_localInstanceId))
        {
            return PageMutationResult<PagePropertyValue>.Failure(new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        if (!await ComputeCanEditAsync(space, page, principal, cancellationToken))
        {
            return PageMutationResult<PagePropertyValue>.Failure(new ForbiddenError("canEdit required"));
        }

        // Input validation AFTER the gates: a caller who may not edit this page gets a
        // refusal, never validation feedback about the payload they sent.
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            return PageMutationResult<PagePropertyValue>.Failure(new ValidationError(
                "A property value cannot be empty. Remove the property instead of clearing it."));
        }

        if (request.Value.Length > MaxValueLength)
        {
            return PageMutationResult<PagePropertyValue>.Failure(new ValidationError(
                $"A property value may be at most {MaxValueLength} characters."));
        }

        // Upsert: the composite PK is (PageId, PagePropertyKeyId), so setting a key the
        // page already carries overwrites its value rather than adding a second row.
        var property = await _db.PageProperties.FirstOrDefaultAsync(
            p => p.PageId == page.Id && p.PagePropertyKeyId == key.Id, cancellationToken);
        if (property is null)
        {
            property = new PageProperty { PageId = page.Id, PagePropertyKeyId = key.Id };
            _db.PageProperties.Add(property);
        }

        property.Value = request.Value;
        property.UpdatedAtUtc = DateTime.UtcNow;
        property.UpdatedByUserId = actingUserId;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PagePropertySetEvent(
            page.Id, space.Id, space.Key, actingUserId, key.Id, key.Key, property.Value));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PagePropertyValue>.Success(
            new PagePropertyValue(key.Id, key.Key, property.Value, key.SortOrder));
    }

    public async Task<PageMutationResult<Guid>> RemoveAsync(
        RemovePagePropertyRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == request.PageId, cancellationToken);
        if (page is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.PageId));
        }

        var key = await _db.PagePropertyKeys.FirstOrDefaultAsync(k => k.Id == request.PagePropertyKeyId, cancellationToken);
        if (key is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(request.PagePropertyKeyId));
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

        if (!await ComputeCanEditAsync(space, page, principal, cancellationToken))
        {
            return PageMutationResult<Guid>.Failure(new ForbiddenError("canEdit required"));
        }

        var property = await _db.PageProperties.FirstOrDefaultAsync(
            p => p.PageId == page.Id && p.PagePropertyKeyId == key.Id, cancellationToken);
        if (property is null)
        {
            // Same non-idempotent stance as LabelService.DetachLabelAsync (removing a
            // label that isn't attached is refused, not quietly accepted): a remove that
            // did nothing is a client bug worth surfacing. The error KIND differs from
            // detach's NotFound on purpose - here both the page and the key exist and
            // were just resolved, so "not found" would be actively misleading about
            // which of the three things is missing.
            return PageMutationResult<Guid>.Failure(new ValidationError(
                $"Page property '{key.Key}' is not set on this page."));
        }

        _db.PageProperties.Remove(property);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PagePropertyRemovedEvent(
            page.Id, space.Id, space.Key, actingUserId, key.Id, key.Key));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Guid>.Success(key.Id);
    }

    public async Task<PageMutationResult<PagePropertyKey>> CreateKeyAsync(
        CreatePagePropertyKeyRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        if (!isInstanceAdmin)
        {
            return PageMutationResult<PagePropertyKey>.Failure(new ForbiddenError("instance admin required"));
        }

        var displayKey = (request.Key ?? string.Empty).Trim();
        if (displayKey.Length == 0)
        {
            return PageMutationResult<PagePropertyKey>.Failure(new ValidationError("A property key cannot be empty."));
        }

        if (displayKey.Length > MaxKeyLength)
        {
            return PageMutationResult<PagePropertyKey>.Failure(new ValidationError(
                $"A property key may be at most {MaxKeyLength} characters."));
        }

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            return PageMutationResult<PagePropertyKey>.Failure(new ValidationError(
                $"A property key description may be at most {MaxDescriptionLength} characters."));
        }

        // Uniqueness is decided on the NORMALIZED form, in the application, so "Owner"
        // and "owner" collide identically on SQL Server and SQLite - see
        // PagePropertyKey.Normalize for why the database's collation must not be the
        // one deciding this.
        var normalized = PagePropertyKey.Normalize(displayKey);
        var existing = await _db.PagePropertyKeys.FirstOrDefaultAsync(k => k.KeyNormalized == normalized, cancellationToken);
        if (existing is not null)
        {
            return PageMutationResult<PagePropertyKey>.Failure(new ValidationError(
                $"A property key '{existing.Key}' already exists."));
        }

        // Appended to the end of the current display order; admins reorder afterwards.
        var nextSortOrder = await _db.PagePropertyKeys.AnyAsync(cancellationToken)
            ? await _db.PagePropertyKeys.MaxAsync(k => k.SortOrder, cancellationToken) + 1
            : 0;

        var key = new PagePropertyKey
        {
            Key = displayKey,
            KeyNormalized = normalized,
            Description = request.Description,
            SortOrder = nextSortOrder,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = actingUserId,
        };
        _db.PagePropertyKeys.Add(key);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PagePropertyKeyCreatedEvent(key.Id, key.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PagePropertyKey>.Success(key);
    }

    public async Task<PageMutationResult<Guid>> DeleteKeyAsync(
        Guid keyId, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        if (!isInstanceAdmin)
        {
            return PageMutationResult<Guid>.Failure(new ForbiddenError("instance admin required"));
        }

        var key = await _db.PagePropertyKeys.FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
        {
            return PageMutationResult<Guid>.Failure(new NotFoundError(keyId));
        }

        // A key in use is REFUSED, never deleted with its values (design.md §20). The
        // count is safe to report: it is a number of rows in an instance-wide registry
        // table, not a list of pages - a per-page answer would leak which restricted
        // pages carry the key, which is exactly what §6.7 forbids, so the count is
        // deliberately as far as this goes.
        var usageCount = await _db.PageProperties.CountAsync(p => p.PagePropertyKeyId == key.Id, cancellationToken);
        if (usageCount > 0)
        {
            return PageMutationResult<Guid>.Failure(new ValidationError(
                $"Property key '{key.Key}' is in use on {usageCount} page(s). Remove those values before deleting the key."));
        }

        _db.PagePropertyKeys.Remove(key);

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new PagePropertyKeyDeletedEvent(key.Id, key.Key, actingUserId));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<Guid>.Success(key.Id);
    }

    private async Task<bool> ComputeCanEditAsync(Space space, Page page, Principal principal, CancellationToken cancellationToken)
    {
        var context = await _permissions.LoadAsync(page, space.IsReplicaOf(_localInstanceId), cancellationToken);
        return context.Compute(principal).CanEdit;
    }
}
