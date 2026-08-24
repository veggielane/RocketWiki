namespace RocketWiki.Api.Audit;

/// <summary>
/// Declares the audit action a user-reachable entry point emits (design.md §7,
/// §8: "every root field... declares its audit action; a schema test fails the
/// build on undeclared fields" — the same rule extended to every channel §7
/// names). Four surfaces carry it: root Query/Mutation fields, MCP tools,
/// client-invokable hub methods, and the minimal-API route handlers on the
/// *Endpoints classes. Each entry point must carry exactly one of this
/// attribute or <see cref="NoAuditAttribute"/> — enforced by
/// AuditCoverageTests in RocketWiki.Api.Tests, not by anything at runtime.
///
/// This is the declaration only. Actual emission — building the
/// <c>AuditEvent</c> row, writing it in the same transaction as a mutation,
/// failing the request if the write fails — happens per channel:
/// AuditFieldMiddleware for root query fields, the domain-event pipeline for
/// mutation successes, explicit <see cref="IAuditSink"/> calls for denials
/// and the hub. Property is allowed alongside Method because Hot Chocolate
/// infers root fields from public properties too — a property-declared field
/// needs somewhere to carry its declaration.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class AuditActionAttribute : Attribute
{
    public AuditActionAttribute(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Audit action must not be empty.", nameof(action));
        }

        Action = action;
    }

    /// <summary>E.g. "page.view", "space.browse", "search.query" (design.md §7).</summary>
    public string Action { get; }
}
