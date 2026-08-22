using System.Reflection;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;

namespace RocketWiki.Api.Audit;

/// <summary>
/// Marks a resolver method for automatic audit dispatch via <see cref="AuditFieldMiddleware"/>.
/// Always paired with <see cref="AuditActionAttribute"/> on the same method — this
/// attribute wires the middleware onto the field; the other declares which action it
/// emits (and satisfies AuditCoverageTests, for root fields). Deliberately opt-in per
/// field rather than a schema-wide <c>UseField</c>: see AuditFieldMiddleware's doc for
/// why Mutation fields never get this.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UseAuditDispatchAttribute : ObjectFieldDescriptorAttribute
{
    protected override void OnConfigure(IDescriptorContext context, IObjectFieldDescriptor descriptor, MemberInfo? member)
    {
        descriptor.Use<AuditFieldMiddleware>();
    }
}
