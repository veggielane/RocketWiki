namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.4.1: delete cascades to the whole live subtree as one audited
/// operation. DeletedPageCount includes the root page itself. Pages that were already
/// soft-deleted independently before this operation are left untouched - they weren't
/// part of "the whole live subtree" and are not counted here.
/// </summary>
public sealed record PageDeleteSummary(Guid RootPageId, int DeletedPageCount, DateTime DeletedAtUtc);

/// <summary>
/// Mirrors PageDeleteSummary for the restore side: brings back exactly the set of pages
/// that were soft-deleted together in one PageDeleteSummary-producing operation
/// (matched by Page.DeleteBatchId), not the page's current live descendants (which may
/// include pages created after the delete).
/// </summary>
public sealed record PageRestoreSummary(Guid RootPageId, int RestoredPageCount);
