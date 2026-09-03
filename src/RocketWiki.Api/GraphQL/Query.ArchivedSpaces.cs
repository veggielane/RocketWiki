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
    /// admin sees every archived space; otherwise the same rule as every other SPACE
    /// listing (<see cref="EffectivePermissionCalculator.IsSpaceVisible"/>, the rule
    /// <c>SpaceReads</c> applies to live spaces): an archived space is listed for a caller
    /// who matches an access grant OR any role grant in it. That is the §6.4/§6.5.2
    /// split applied consistently — being told a space exists (and was archived) is
    /// space visibility, which access and roles both confer; whether the caller may then
    /// restore it is <c>SpaceService.RestoreAsync</c>'s own manage gate, which refuses
    /// anyone but that space's space-admin or an instance admin. A listing wider than the
    /// mutation is fine; a listing that hid a space from its own former readers would be
    /// the one thing archiving should not silently do to them.
    ///
    /// Absent-not-forbidden (design.md §6.7): a caller no grant admits gets an empty
    /// list, indistinguishable from "nothing is archived". A listing the caller was
    /// allowed to make is not a denial of each absent item (see SpaceReads' doc), so no
    /// per-space denial rows are written. No content field hangs off this projection, so
    /// nothing here shows a page to a caller whose access grant is absent.
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
            // Both grant kinds, like SpaceReads: an access grant or any role grant lists
            // the space (design.md §6.4/§6.5.2).
            var archivedIds = archived.Select(s => s.Id).ToList();
            var grants = await db.AccessRules
                .Include(r => r.Selectors)
                .Where(r => (r.Kind == AccessRuleKind.RoleGrant || r.Kind == AccessRuleKind.AccessGrant)
                    && r.SpaceId != null && archivedIds.Contains(r.SpaceId.Value))
                .ToListAsync(cancellationToken);

            archived = archived
                .Where(s => EffectivePermissionCalculator.IsSpaceVisible(grants.Where(g => g.SpaceId == s.Id), principal))
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
