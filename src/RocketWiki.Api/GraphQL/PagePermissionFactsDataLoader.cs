using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches the viewer-permission facts behind <c>Page.canEdit/canComment/
/// canManageAccess</c> (design.md §8's DataLoader requirement). This one, unlike
/// <see cref="PageByIdDataLoader"/>, is a TRUE batch: one
/// <see cref="IPagePermissionReadService.GetPermissionFactsAsync"/> call — a constant
/// number of queries — covers every page in the batch, so a `children { canEdit }`
/// list is not N ancestor walks. The loader's per-request cache additionally means
/// the three fields on one page share a single computation, and a page reached by
/// several paths in one query is computed once.
///
/// A key with no entry (page vanished or now fails canView — a mid-request rule
/// change race, since every Page reaching these fields already passed canView to
/// resolve at all) yields null here, which the field resolvers map to false:
/// fail closed, never default-allow (design.md §6).
/// </summary>
public sealed class PagePermissionFactsDataLoader : BatchDataLoader<Guid, PagePermissionFacts>
{
    private readonly DbContextOptions<RocketWikiDbContext> _dbOptions;
    private readonly InstanceIdentity _instanceIdentity;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public PagePermissionFactsDataLoader(
        DbContextOptions<RocketWikiDbContext> dbOptions,
        InstanceIdentity instanceIdentity,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbOptions = dbOptions;
        _instanceIdentity = instanceIdentity;
        _principalAccessor = principalAccessor;
    }

    protected override async Task<IReadOnlyDictionary<Guid, PagePermissionFacts>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            // No token, no permissions - every key resolves to "missing", which the
            // field resolvers read as deny (design.md §6.1: no anonymous access).
            return new Dictionary<Guid, PagePermissionFacts>();
        }

        // Own context per batch — see DataLoaderDbContext. Constructed here rather
        // than injected for the same reason as PageMarkingByPageIdDataLoader:
        // injecting the service would carry the request-scoped context in with it,
        // which is the thing that raced. Mirrors Program's registration exactly.
        await using var db = DataLoaderDbContext.Create(_dbOptions);
        var permissionReadService = new PagePermissionReadService(db, _instanceIdentity.LocalInstanceId);

        return await permissionReadService.GetPermissionFactsAsync(keys.ToArray(), principal, cancellationToken);
    }
}
