namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: Label — free-form tag, space-scoped.</summary>
public class Label
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SpaceId { get; set; }
    public Space? Space { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<PageLabel> PageLabels { get; set; } = new List<PageLabel>();
}
