using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type, same reasoning as <see cref="PageType"/>: implicit inference would
/// expose <c>Space</c>'s raw navigation collections (<c>Pages</c>, <c>AccessRules</c>,
/// <c>Homepage</c>) directly off EF, bypassing every authorization check this schema
/// otherwise enforces on those same objects elsewhere (a raw <c>Pages</c> collection
/// would leak restricted pages' existence; a raw <c>AccessRules</c> collection would
/// leak rule expressions to non-admins). Each is ignored here and re-exposed only
/// through <see cref="SpaceFieldResolvers"/>, which applies the check design.md §8's
/// schema sketch calls for ("grants: [SpaceGrant!]! # space-admin only") or routes
/// through <c>IPageReadService</c> the same way every other Page-returning field does.
/// </summary>
public sealed class SpaceType : ObjectType<Space>
{
    protected override void Configure(IObjectTypeDescriptor<Space> descriptor)
    {
        descriptor.Ignore(s => s.Pages);
        descriptor.Ignore(s => s.AccessRules);
        descriptor.Ignore(s => s.Labels);
        descriptor.Ignore(s => s.Homepage);

        // Implicit inference used to export the entity's IsReplicaOf(localInstanceId)
        // helper as an argument-taking field no browser could call - the client has no
        // way to know the local instance id (design.md §8's known-deltas list; nothing
        // in the SPA or the tests ever queried it). Removed in favor of the
        // server-resolved `isReplica` below.
        descriptor.Ignore(s => s.IsReplicaOf(default!));

        // design.md §12: the client-usable replica flag, computed server-side against
        // the configured InstanceIdentity so the "mirrored from X - read-only" banner
        // needs no admin-only lookup. `originInstanceId` (already exposed above via
        // inference, and already handed to every viewer through ReadOnlyReplicaError)
        // names the origin the banner shows; exposing it to all viewers of the space
        // is deliberate - see GetIsReplica's doc.
        descriptor.Field("isReplica")
            .Type<NonNullType<BooleanType>>()
            .ResolveWith<SpaceFieldResolvers>(r => r.GetIsReplica(default!, default!));

        // Viewer-relative watch state - the caller's own Watch row, no audit of its
        // own (see ViewerWatchesSpaceDataLoader's doc).
        descriptor.Field("viewerIsWatching")
            .Type<NonNullType<BooleanType>>()
            .ResolveWith<SpaceFieldResolvers>(r => r.GetViewerIsWatchingAsync(default!, default!, default));

        descriptor.Field("homepage")
            .Type<PageType>()
            .ResolveWith<SpaceFieldResolvers>(r => r.GetHomepageAsync(default!, default!, default!, default!, default));

        descriptor.Field("grants")
            .Type<NonNullType<ListType<NonNullType<AccessRuleType>>>>()
            .ResolveWith<SpaceFieldResolvers>(r => r.GetGrantsAsync(default!, default!, default!, default!, default));

        descriptor.Field("trashedPages")
            .Type<NonNullType<ListType<NonNullType<PageType>>>>()
            .ResolveWith<SpaceFieldResolvers>(r => r.GetTrashedPagesAsync(default!, default!, default!, default!, default));
    }
}
