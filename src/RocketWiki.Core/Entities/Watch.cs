namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: Watch — explicit subscription to a space or a single page (exactly one).</summary>
public class Watch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid? SpaceId { get; set; }
    public Space? Space { get; set; }
    public Guid? PageId { get; set; }
    public Page? Page { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
