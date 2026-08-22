namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md: Comment — page-level threaded comments. IsDeleted is a tombstone
/// (body blanked by the application) that keeps thread shape, not a filtered soft delete.
/// </summary>
public class Comment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PageId { get; set; }
    public Page? Page { get; set; }
    public Guid? ParentCommentId { get; set; }
    public Comment? ParentComment { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid AuthorUserId { get; set; }
    public User? Author { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public bool IsDeleted { get; set; }

    public ICollection<Comment> Replies { get; set; } = new List<Comment>();
}
