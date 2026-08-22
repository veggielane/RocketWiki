using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Ignores the raw <c>Space</c>/<c>Page</c> navigation properties for the same reason
/// every other explicit type in this schema does: they would bypass every
/// authorization check the rest of the schema enforces on those objects. An
/// <c>AccessRule</c> is only ever reached through <see cref="SpaceFieldResolvers.GetGrantsAsync"/>,
/// which is already space-admin/instance-admin gated, so nothing further is
/// suppressed here beyond the two navigation properties.
/// </summary>
public sealed class AccessRuleType : ObjectType<AccessRule>
{
    protected override void Configure(IObjectTypeDescriptor<AccessRule> descriptor)
    {
        descriptor.Ignore(r => r.Space);
        descriptor.Ignore(r => r.Page);
    }
}
