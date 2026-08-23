using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit for the same reason as <see cref="PageRevisionType"/>/<see cref="CommentType"/>:
/// <c>Attachment.UploadedBy</c> navigates to <c>User</c>, which implicit inference would
/// expose in full, including `AttributesJson` (nationality - admin-only, design.md §6.2).
/// The navigation is rebound to a display-safe <see cref="UserRef"/> (id + display
/// name, batched) alongside the plain `UploadedByUserId`. `StorageKey` is ignored - it's an opaque
/// internal detail of `IFileStorage` (design.md §10), not something a client has any use
/// for, and exposing it would be one step closer to someone trying to fetch it directly
/// instead of through the authorized/audited download route.
/// </summary>
public sealed class AttachmentType : ObjectType<Attachment>
{
    protected override void Configure(IObjectTypeDescriptor<Attachment> descriptor)
    {
        descriptor.Ignore(a => a.Page);
        descriptor.Ignore(a => a.StorageKey);

        // Rebound, not ignored-then-redefined: `UploadedBy` camelCases to exactly the
        // `uploadedBy` field name being defined, and Ignore() wins permanently over a
        // later .Field() of the same name (see PageType's comment on this trap).
        // The rebind replaces the raw User navigation with the display-safe slice:
        // id + display name, batched, never the User entity (see
        // UserRefFieldResolvers' doc).
        descriptor.Field(a => a.UploadedBy)
            .Type<NonNullType<ObjectType<UserRef>>>()
            .ResolveWith<UserRefFieldResolvers>(r => r.GetUploadedByAsync(default!, default!, default));
    }
}
