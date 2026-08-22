using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type (not implicit inference) for exactly one reason: `PageRevision.Author`
/// navigates to <c>User</c>, and implicit inference would expose every public property
/// on it — including `AttributesJson`, the mirrored registered attributes (nationality)
/// design.md §6.2 marks admin-visible only. Ignoring the navigation here is what keeps
/// that off the schema; only the plain `AuthorUserId` Guid is exposed.
/// </summary>
public sealed class PageRevisionType : ObjectType<PageRevision>
{
    protected override void Configure(IObjectTypeDescriptor<PageRevision> descriptor)
    {
        descriptor.Ignore(r => r.Page);
        descriptor.Ignore(r => r.Author);
    }
}
