using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Reads;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// design.md §8 schema sketch: "spaces: [Space!]! # only spaces the caller can
    /// view". The visibility logic itself lives in <see cref="SpaceReads"/>, shared
    /// with the MCP <c>list_spaces</c> tool (design.md §8: same code path on every
    /// channel); see <see cref="SpaceFieldResolvers"/>'s own doc for why computing
    /// space visibility at this layer — rather than a Core read service — is the
    /// expected pattern here, not a Page-style shortcut.
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<Space>> Spaces(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        return await SpaceReads.GetViewableSpacesAsync(db, principal, cancellationToken);
    }

    /// <summary>Same "absent, not forbidden" convention as Page (design.md §6.7): null
    /// for a space that doesn't exist, is archived, or the caller holds no role in —
    /// the three are not distinguished from one another (see <see cref="SpaceReads"/>).
    /// Same audit split as Query.PageTree, too (§7): a *specific-space* lookup that
    /// fails visibility is a refused browse, recorded as Denied with the rule engine's
    /// no-space-role reason before it collapses to the identical null a missing key
    /// gets. AuditFieldMiddleware never audits a null result, so the Denied row below
    /// is the only row a refused lookup produces.</summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<Space?> Space(
        string key,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        var result = await SpaceReads.GetViewableSpaceByKeyAsync(db, principal, key, cancellationToken);
        if (result is SpaceReadResult.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "space.browse", AuditSubjectType.Space, denied.SpaceId, denied.Reason, cancellationToken);
        }

        return (result as SpaceReadResult.Found)?.Space;
    }
}
