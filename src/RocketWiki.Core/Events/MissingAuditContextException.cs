namespace RocketWiki.Core.Events;

/// <summary>
/// Thrown when a unit of work has raised a domain event but no <see cref="AuditContext"/>
/// was ever set. design.md §7: "if the audit insert fails, the request fails" - fail
/// closed applies before any SQL is even sent, not just to a failed insert. There is no
/// default AuditContext and none should ever be added; a mutation with nowhere to route
/// its audit record must not proceed.
/// </summary>
public sealed class MissingAuditContextException : Exception
{
    public MissingAuditContextException()
        : base("A domain event was raised but no AuditContext was set - the mutation cannot be audited, so it must not proceed.")
    {
    }
}
