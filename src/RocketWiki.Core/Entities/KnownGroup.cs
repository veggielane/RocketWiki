using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: KnownGroup — rule-builder picker source.</summary>
public class KnownGroup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public KnownGroupSource Source { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
}
