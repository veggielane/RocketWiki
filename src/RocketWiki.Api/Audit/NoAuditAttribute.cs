namespace RocketWiki.Api.Audit;

/// <summary>
/// Explicitly declares that a user-reachable entry point (root Query/Mutation
/// field, MCP tool, hub method, or minimal-API route handler) emits no audit
/// event, with a reason. Use sparingly: design.md §7 requires every user
/// action — reads and writes, successes and denials — to be audited. This
/// exists for the rare entry point that isn't a user action on wiki content
/// at all (echoing the caller's own already-validated token claims back to
/// them; serving display assets like avatars and emoji images).
///
/// Paired with <see cref="AuditActionAttribute"/>: AuditCoverageTests requires
/// every entry point to carry exactly one of the two, so none can ship
/// silently undeclared either way.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class NoAuditAttribute : Attribute
{
    public NoAuditAttribute(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to justify skipping audit.", nameof(reason));
        }

        Reason = reason;
    }

    public string Reason { get; }
}
