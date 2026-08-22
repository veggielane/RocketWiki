namespace RocketWiki.Core.Services;

public sealed record CreatePageRequest(Guid SpaceId, Guid? ParentPageId, string Slug, string Title, string Content);

public sealed record UpdatePageContentRequest(Guid PageId, int ExpectedRevisionNumber, string Title, string Content, string? EditSummary);

public sealed record MovePageRequest(Guid PageId, Guid? NewParentPageId, int NewSortOrder);

public sealed record DeletePageRequest(Guid PageId);

/// <summary>Targets the root of a previously subtree-deleted page. See RestorePageSummary for what "the subtree" means here.</summary>
public sealed record RestorePageRequest(Guid PageId);

public sealed record RestoreRevisionRequest(Guid PageId, int RevisionNumberToRestore, int ExpectedCurrentRevisionNumber);
