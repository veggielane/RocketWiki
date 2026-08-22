using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Events;

/// <summary>
/// Request/channel metadata for the audit row a domain event produces (design.md §7).
/// Deliberately separate from IDomainEvent: this is per-request infrastructure data
/// (which channel, which request, which client IP), not a domain fact, and keeping it
/// out of the event records means unit tests can construct events without fabricating
/// HTTP context.
/// </summary>
public sealed record AuditContext(AuditChannel Channel, string RequestId, string ClientIp, string? McpClient = null);
