namespace RocketWiki.Api.Audit;

/// <summary>
/// Declares the audit action a root Query/Mutation field emits (design.md §7,
/// §8: "every root field... declares its audit action; a schema test fails the
/// build on undeclared fields"). Every root field must carry exactly one of
/// this attribute or <see cref="NoAuditAttribute"/> — enforced by
/// AuditCoverageTests in RocketWiki.Api.Tests, not by anything at runtime.
///
/// This is the declaration only. Actual emission — building the
/// <c>AuditEvent</c> row, writing it in the same transaction as a mutation,
/// failing the request if the write fails — is the domain-event pipeline
/// another agent is building in RocketWiki.Core/RocketWiki.Data. See
/// <see cref="IAuditSink"/> for the seam this API layer will call into once
/// that exists.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
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
