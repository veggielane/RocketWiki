using HotChocolate.Types;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Services;

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
        // Same reasoning as PageLabels: the raw join rows carry Page/PagePropertyKey
        // navigations, and the field below re-exposes them as a flat projection instead
        // (its camelCase name `pageProperties` doesn't collide with `properties`, so
        // ignoring it first is harmless).
        descriptor.Ignore(p => p.PageProperties);
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

        // design.md §6.7/§21.8: the placeholder for a parent the caller cannot view,
        // beside the unchanged `parent` (null on denied). Shares the loader with it.
        descriptor.Field("parentDenial")
            .Type<ObjectType<AccessDenialView>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetParentDenialAsync(default!, default!, default!, default!, default!, default));

        descriptor.Field("children")
            .Type<NonNullType<ListType<NonNullType<PageType>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetChildrenAsync(default!, default!, default!, default!, default));

        // design.md §6.7/§21.8: the targets of this page's own page:// links, each a
        // Page, a placeholder, or missing. Derived from the content, never from an
        // argument - see the resolver for why that is load-bearing.
        descriptor.Field("linkTargets")
            .Type<NonNullType<ListType<NonNullType<ObjectType<PageLinkTarget>>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetLinkTargetsAsync(default!, default!, default!, default!, default));

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

        // Additive next to the names-only `labels` (which shipped SPA operations
        // already select): id + name, so attachLabel/detachLabel become reachable
        // end to end. See Query.labelDetails' doc.
        descriptor.Field("labelDetails")
            .Type<NonNullType<ListType<NonNullType<ObjectType<LabelRef>>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetLabelDetailsAsync(default!, default!, default));

        // Structured key/value metadata (design.md §20). Visible to anyone who can view
        // the page — properties carry no restriction of their own, same as labels
        // (§6.4.2) — and batched through a grouped DataLoader so a list of pages costs
        // one PageProperties query.
        descriptor.Field("properties")
            .Type<NonNullType<ListType<NonNullType<ObjectType<PagePropertyValue>>>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetPropertiesAsync(default!, default!, default));

        // The protective marking (design.md §21). Rebinds the raw Marking navigation
        // rather than ignoring it, for the same reason Revisions/Comments/Attachments are
        // rebound: its camelCase name IS this field's name, and Ignore() would win
        // permanently over a later Field() of the same name. The entity - which carries a
        // Page navigation - is never exposed; PageMarkingView is.
        //
        // NonNull: every page carries a marking, and a nullable field here would invite a
        // client to render "unclassified" for a page whose marking row went missing,
        // which the read path treats as TOP SECRET. `label` on the payload is the single
        // server-built display string, so the SPA and an MCP client render identical text.
        descriptor.Field(p => p.Marking)
            .Type<NonNullType<ObjectType<PageMarkingView>>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetMarkingAsync(default!, default!, default!, default));

        // Viewer-relative watch state (design.md §8's known-deltas list); display of
        // the caller's own Watch row, no audit of its own - see the resolver's doc.
        descriptor.Field("viewerIsWatching")
            .Type<NonNullType<BooleanType>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetViewerIsWatchingAsync(default!, default!, default));

        // Who trashed this page, for the trash listing - display-only resolution of
        // DeletedByUserId via the local User mirror (see UserRefFieldResolvers' doc).
        descriptor.Field("deletedBy")
            .Type<ObjectType<UserRef>>()
            .ResolveWith<UserRefFieldResolvers>(r => r.GetDeletedByAsync(default!, default!, default));

        // The SPA's contract (schema.placeholder.graphql / operations/search.graphql)
        // addresses pages by space KEY, not space id — search results link as
        // /spaces/{spaceKey}/... — so Page carries its space's key directly.
        descriptor.Field("spaceKey")
            .Type<NonNullType<StringType>>()
            .ResolveWith<PageFieldResolvers>(r => r.GetSpaceKeyAsync(default!, default!, default));

        // --- Viewer-permission fields (design.md §6.6/§8 "known deltas") ---
        // Facts about the CURRENT caller, batched through PagePermissionFactsDataLoader
        // so lists cost a constant number of queries; audit stance and leak analysis
        // live on PagePermissionFieldResolvers. `restrictions` rebinds the raw
        // Restrictions navigation the same way Revisions/Comments/Attachments are
        // rebound above (its camelCase name is exactly this field's name, and Ignore()
        // would win permanently over a later Field() of the same name) — the raw
        // ICollection<AccessRule> is never exposed.
        descriptor.Field("canEdit")
            .Type<NonNullType<BooleanType>>()
            .ResolveWith<PagePermissionFieldResolvers>(r => r.GetCanEditAsync(default!, default!, default));

        descriptor.Field("canComment")
            .Type<NonNullType<BooleanType>>()
            .ResolveWith<PagePermissionFieldResolvers>(r => r.GetCanCommentAsync(default!, default!, default));

        descriptor.Field("canManageAccess")
            .Type<NonNullType<BooleanType>>()
            .ResolveWith<PagePermissionFieldResolvers>(r => r.GetCanManageAccessAsync(default!, default!, default!, default));

        descriptor.Field(p => p.Restrictions)
            .Type<NonNullType<ListType<NonNullType<ObjectType<PageRestrictionDetail>>>>>()
            .ResolveWith<PagePermissionFieldResolvers>(r => r.GetRestrictionsAsync(default!, default!, default!, default!, default));
    }
}
