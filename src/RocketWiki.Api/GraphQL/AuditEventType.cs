using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type: <c>AuditEvent.User</c> is a raw navigation to the local <c>User</c>
/// row, whose <c>AttributesJson</c> is "admin-visible only" (that entity's own doc
/// comment) — but even for this admin-only query, exposing the full row is more than
/// the audit viewer needs (design.md §6.6's permission inspector, not this screen,
/// is where attribute detail belongs). <c>userDisplayName</c> is resolved separately
/// instead, so nothing beyond a display label and the id ever reaches the client.
/// </summary>
public sealed class AuditEventType : ObjectType<AuditEvent>
{
    protected override void Configure(IObjectTypeDescriptor<AuditEvent> descriptor)
    {
        descriptor.Ignore(e => e.User);

        descriptor.Field("userDisplayName")
            .Type<StringType>()
            .ResolveWith<AuditEventFieldResolvers>(r => r.GetUserDisplayNameAsync(default!, default!, default));
    }
}
