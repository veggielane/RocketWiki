namespace RocketWiki.Core.Enums;

/// <summary>data-model.md: AuditEvent.Channel.</summary>
public enum AuditChannel : byte
{
    GraphQl = 1,
    Mcp = 2,
    Attachment = 3,
    Sync = 4,
    System = 5,
}
