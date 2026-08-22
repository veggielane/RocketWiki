using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit for the same reason as <see cref="PageRevisionType"/>/<see cref="CommentType"/>:
/// <c>Attachment.UploadedBy</c> navigates to <c>User</c>, which implicit inference would
/// expose in full, including `AttributesJson` (nationality - admin-only, design.md §6.2).
/// Only `UploadedByUserId` is exposed. `StorageKey` is also ignored - it's an opaque
/// internal detail of `IFileStorage` (design.md §10), not something a client has any use
/// for, and exposing it would be one step closer to someone trying to fetch it directly
/// instead of through the authorized/audited download route.
/// </summary>
public sealed class AttachmentType : ObjectType<Attachment>
{
    protected override void Configure(IObjectTypeDescriptor<Attachment> descriptor)
    {
        descriptor.Ignore(a => a.Page);
        descriptor.Ignore(a => a.UploadedBy);
        descriptor.Ignore(a => a.StorageKey);
    }
}
