namespace RocketWiki.Core.Enums;

/// <summary>data-model.md: AuditEvent.Channel.</summary>
public enum AuditChannel : byte
{
    GraphQl = 1,
    Mcp = 2,
    Attachment = 3,
    Sync = 4,
    System = 5,

    /// <summary>
    /// The SignalR hub surface (design.md §8). Minted for the co-editing session
    /// actions (page.edit_session.*): a hub invocation reaches no HTTP path
    /// CurrentAuditContextAccessor maps, and filing it under GraphQl would be a lie
    /// about which transport carried the action. Presence remains unaudited (§8),
    /// so this channel only ever carries edit-session rows.
    /// </summary>
    Realtime = 6,
}
