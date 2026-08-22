namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: PageRevision — immutable, never updated or deleted.</summary>
public class PageRevision
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PageId { get; set; }
    public Page? Page { get; set; }

    /// <summary>1-based, dense per page.</summary>
    public int RevisionNumber { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? EditSummary { get; set; }
    public Guid AuthorUserId { get; set; }
    public User? Author { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
