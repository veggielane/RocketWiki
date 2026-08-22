using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: AttributeDefinition — the attribute registry (design.md §6.2).</summary>
public class AttributeDefinition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Key { get; set; } = string.Empty;
    public string ClaimName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AttributeValueType Type { get; set; }
    public string? AllowedValuesJson { get; set; }
}
