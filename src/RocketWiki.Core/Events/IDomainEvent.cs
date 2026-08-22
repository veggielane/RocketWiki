namespace RocketWiki.Core.Events;

/// <summary>
/// design.md §7/§12: "all mutations flow through the domain-event pipeline" - this is
/// the marker every such event implements. A domain event carries only domain facts
/// (who did what, to which subject); request/channel metadata (RequestId, ClientIp,
/// Channel, McpClient) is supplied separately via <see cref="AuditContext"/> at the
/// point a unit of work commits, so domain events stay pure and testable without any
/// HTTP/transport concern leaking into Core.
/// </summary>
public interface IDomainEvent
{
    /// <summary>The user who performed the action. Null only for system-initiated events (e.g. sync import).</summary>
    Guid? ActorUserId { get; }
}
