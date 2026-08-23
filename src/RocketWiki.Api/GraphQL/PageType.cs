using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type, not implicit inference over the entity — deliberately. Implicit
/// inference would expose <c>Page</c>'s raw navigation collections
/// (<c>ChildPages</c>, <c>ParentPage</c>, <c>Revisions</c>, ...) directly off EF,
/// bypassing <see cref="PageFieldResolvers"/>'s canView checks entirely: exactly the
/// leak design.md §6.7 and §8 warn about ("a field middleware... UseProjection straight
/// onto an EF queryable can return page data before your middleware runs"). Every one
/// of those navigations is ignored here and re-exposed only through the resolver
/// methods that route through IPageReadService (or, for Comments/Attachments/Labels -
/// which carry no restriction of their own, only the parent Page's - a direct but
/// still explicit, hand-written query in PageFieldResolvers).
/// </summary>
public sealed class PageType : ObjectType<Page>
{
    protected override void Configure(IObjectTypeDescriptor<Page> descriptor)
    {
        descriptor.Ignore(p => p.Space);
        descriptor.Ignore(p => p.ParentPage);
        descriptor.Ignore(p => p.ChildPages);
        descriptor.Ignore(p => p.PageLabels);
        descriptor.Ignore(p => p.Restrictions);
        // Not a leak (ancestor ids of an already-viewable page aren't sensitive) - just
        // an unintended inference from the public GetAncestorIds() method that's tidier
        // left off an intentionally hand-curated schema.
        descriptor.Ignore(p => p.GetAncestorIds());

        // Revisions/Attachments/Comments are NOT ignored above, unlike the navigations
        // that are: their camelCased names (revisions/attachments/comments) are exactly
        // the field names being redefined below, and Hot Chocolate's Ignore() wins
        // permanently over a later .Field() call for the same name (unlike ChildPages/
        // ParentPage/PageLabels, whose camelCase names don't collide with
        // children/parent/labels, so ignoring them first is harmless). Rebinding the
        // property directly replaces it instead of ignoring-then-redefining.

        descriptor.Field(p => p.CurrentContent)
            .Name("content")
            .Type<NonNullType<StringType>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetContent(default!));

        descriptor.Field("parent")
            .Type<PageType>()
            .ResolveWith<PageFieldResolvers>(r => r.GetParentAsync(default!, default!, default!, default!, default));

        descriptor.Field("children")
            .Type<NonNullType<ListType<NonNullType<PageType>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetChildrenAsync(default!, default!, default!, default!, default!, default));

        descriptor.Field(p => p.Revisions)
            .Type<NonNullType<ListType<NonNullType<PageRevisionType>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetRevisionsAsync(default!, default!, default!, default!, default));

        descriptor.Field(p => p.Comments)
            .Type<NonNullType<ListType<NonNullType<CommentType>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetCommentsAsync(default!, default!, default));

        descriptor.Field(p => p.Attachments)
            .Type<NonNullType<ListType<NonNullType<AttachmentType>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetAttachmentsAsync(default!, default!, default));

        descriptor.Field("labels")
            .Type<NonNullType<ListType<NonNullType<StringType>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetLabelsAsync(default!, default!, default));

        // The SPA's contract (schema.placeholder.graphql / operations/search.graphql)
        // addresses pages by space KEY, not space id — search results link as
        // /spaces/{spaceKey}/... — so Page carries its space's key directly.
        descriptor.Field("spaceKey")
            .Type<NonNullType<StringType>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetSpaceKeyAsync(default!, default!, default));
    }
}
