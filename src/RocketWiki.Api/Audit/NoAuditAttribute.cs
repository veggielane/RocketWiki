namespace RocketWiki.Api.Audit;

/// <summary>
/// Explicitly declares that a root Query/Mutation field emits no audit event,
/// with a reason. Use sparingly: design.md §7 requires every user action —
/// reads and writes, successes and denials — to be audited. This exists for
/// the rare root field that isn't a user action on wiki content at all (e.g.
/// echoing the caller's own already-validated token claims back to them).
///
/// Paired with <see cref="AuditActionAttribute"/>: AuditCoverageTests requires
/// every root field to carry exactly one of the two, so a field can't ship
/// silently undeclared either way.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
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
