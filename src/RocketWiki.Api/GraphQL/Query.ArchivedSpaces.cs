using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Flat projection for the archived-spaces admin listing — deliberately not the
/// <c>Space</c> type: an archived space is hidden from every normal read path (the EF
/// query filter), and returning the full Space here would re-open its tree/trash/
/// grants fields on an object the rest of the schema treats as absent. Id, key, name
/// and when it was archived are exactly what the restore screen needs
/// (web/src/pages/ArchivedSpacesPage.tsx) and nothing more.
/// </summary>
[GraphQLName("ArchivedSpace")]
public sealed record ArchivedSpaceView(Guid Id, string Key, string Name, DateTime ArchivedAtUtc);

public partial class Query
{
    /// <summary>
    /// The listing that feeds <c>restoreSpace</c> (design.md §6.5.1). Scope: instance
    /// admin sees every archived space; otherwise only spaces whose grants compute the
    /// caller as that space's own <c>space-admin</c> — exactly the set of spaces
    /// <c>SpaceService.RestoreAsync</c> would let them restore, so the listing offers
    /// nothing the mutation would refuse. This is the conservative reading of §6.5.1's
    /// open question ("read-only-but-visible to their existing viewers, or hidden from
    /// everyone except admins?"): viewers and editors see nothing here, matching
    /// <c>SpaceReads</c>' existing exclusion of archived spaces from browse — widening
    /// archived visibility to viewers stays a deliberate future decision, not a side
    /// effect of adding a restore listing.
    ///
    /// Absent-not-forbidden (design.md §6.7): a caller with nothing to restore gets an
    /// empty list, indistinguishable from "nothing is archived". A listing the caller
    /// was allowed to make is not a denial of each absent item (see SpaceReads' doc),
    /// so no per-space denial rows are written.
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<ArchivedSpaceView>> ArchivedSpaces(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        var archived = await db.Spaces.IgnoreQueryFilters()
            .Where(s => s.IsDeleted)
            .OrderBy(s => s.Key)
            .ToListAsync(cancellationToken);
        if (archived.Count == 0)
        {
            return [];
        }

        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            var archivedIds = archived.Select(s => s.Id).ToList();
            var grants = await db.AccessRules
                .Where(r => r.Kind == AccessRuleKind.SpaceGrant && r.SpaceId != null && archivedIds.Contains(r.SpaceId.Value))
                .ToListAsync(cancellationToken);

            archived = archived
                .Where(s => EffectivePermissionCalculator.ComputeSpaceRole(
                    grants.Where(g => g.SpaceId == s.Id), principal) == SpaceRole.SpaceAdmin)
                .ToList();
        }

        // ArchiveAsync always stamps DeletedAtUtc alongside IsDeleted; a null here would
        // be corrupt state, surfaced loudly rather than defaulted away.
        return archived
            .Select(s => new ArchivedSpaceView(
                s.Id,
                s.Key,
                s.Name,
                s.DeletedAtUtc ?? throw new InvalidOperationException(
                    $"Archived space {s.Id} has no DeletedAtUtc - IsDeleted and DeletedAtUtc must be set together.")))
            .ToList();
    }
}
