namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: Page — belongs to a space, forms a tree via ParentPageId.</summary>
public class Page
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SpaceId { get; set; }
    public Space? Space { get; set; }
    public Guid? ParentPageId { get; set; }
    public Page? ParentPage { get; set; }

    /// <summary>Materialized path of ancestor ids, "/id1/id2/" — see data-model.md.</summary>
    public string AncestorPath { get; set; } = "/";

    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int CurrentRevisionNumber { get; set; }
    public string CurrentContent { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }

    /// <summary>
    /// Groups the pages trashed together by one cascade delete (design.md §6.4.1).
    /// Null for a live page. Restore correlates by this, not by DeletedAtUtc +
    /// DeletedByUserId, which would wrongly merge two independent deletes by the same
    /// actor landing in the same millisecond.
    /// </summary>
    public Guid? DeleteBatchId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<Page> ChildPages { get; set; } = new List<Page>();
    public ICollection<PageRevision> Revisions { get; set; } = new List<PageRevision>();
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<PageLabel> PageLabels { get; set; } = new List<PageLabel>();

    /// <summary>Page-restriction AccessRules attached directly to this page (not ancestors).</summary>
    public ICollection<AccessRule> Restrictions { get; set; } = new List<AccessRule>();

    /// <summary>
    /// Ancestor ids parsed from <see cref="AncestorPath"/>, root-most first. Does not
    /// include this page's own id. Restrictions accumulate down this chain (design.md §6.4).
    /// </summary>
    public IReadOnlyList<Guid> GetAncestorIds()
    {
        if (string.IsNullOrEmpty(AncestorPath) || AncestorPath == "/")
        {
            return Array.Empty<Guid>();
        }

        return AncestorPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Guid.Parse)
            .ToArray();
    }
}
