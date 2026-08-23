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
