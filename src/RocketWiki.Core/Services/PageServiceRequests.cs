using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

public sealed record CreatePageRequest(
    Guid SpaceId, Guid? ParentPageId, string Slug, string Title, string Content, PageIcon? Icon = null);

/// <summary>
/// <paramref name="Icon"/> is the page's icon AFTER the save, null meaning "no icon".
/// It rides on the content save rather than getting its own mutation because it is
/// page metadata the author sets while editing, exactly like the title beside it —
/// and because a separate mutation would let the icon and the content disagree about
/// which revision they belong to.
/// </summary>
public sealed record UpdatePageContentRequest(
    Guid PageId, int ExpectedRevisionNumber, string Title, string Content, string? EditSummary,
    PageIcon? Icon = null);

public sealed record MovePageRequest(Guid PageId, Guid? NewParentPageId, int NewSortOrder);

public sealed record DeletePageRequest(Guid PageId);

/// <summary>Targets the root of a previously subtree-deleted page. See RestorePageSummary for what "the subtree" means here.</summary>
public sealed record RestorePageRequest(Guid PageId);

public sealed record RestoreRevisionRequest(Guid PageId, int RevisionNumberToRestore, int ExpectedCurrentRevisionNumber);
