using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.5.1: Create requires instance `admin` - nothing else can authorize it,
/// since a space that doesn't exist yet has no grants to evaluate a role against. Create
/// also takes the space's <see cref="InitialSpaceGrant"/> and commits it in the SAME
/// transaction as the Space row - see that type's doc comment for why: without this, a
/// new space could exist with zero grants and no way for anyone to ever create the
/// first one, since AccessRuleService.CreateAsync's own space-admin check has nothing to
/// evaluate against on a space that has never had a grant.
///
/// Rename, Archive, and Restore all accept EITHER instance `admin` OR that space's own
/// `space-admin` (evaluated the normal way via EffectivePermissionCalculator), matching
/// `space-admin` meaning "manage this space". <c>isInstanceAdmin</c> is a plain bool the
/// caller resolves from the token's realm/client roles, exactly like
/// <c>actingUserId</c> is already caller-resolved from the `sub` claim (design.md §11) -
/// this service has no way to derive it itself.
///
/// Archive does not cascade to pages (design.md §6.5.1): it hides the space from browse
/// and makes it read-only, content untouched, and Restore reverses it - unlike page
/// delete, which removes content and therefore needs canEdit per page.
/// </summary>
public interface ISpaceService
{
    Task<PageMutationResult<Space>> CreateAsync(
        CreateSpaceRequest request, InitialSpaceGrant initialGrant, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Space>> RenameAsync(
        RenameSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Space>> ArchiveAsync(
        ArchiveSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Space>> RestoreAsync(
        RestoreSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
