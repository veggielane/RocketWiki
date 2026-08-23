using GreenDonut;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;

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
    private readonly IPagePermissionReadService _permissionReadService;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public PagePermissionFactsDataLoader(
        IPagePermissionReadService permissionReadService,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _permissionReadService = permissionReadService;
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

        return await _permissionReadService.GetPermissionFactsAsync(keys.ToArray(), principal, cancellationToken);
    }
}
