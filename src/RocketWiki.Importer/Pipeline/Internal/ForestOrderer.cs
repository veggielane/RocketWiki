namespace RocketWiki.Importer.Pipeline.Internal;

/// <summary>
/// Generic parent-before-child ordering (breadth-first from the roots) over anything with
/// an id and an optional parent id. Shared by <see cref="ImportTreePlanner"/> (the
/// space-wide page tree) and comment-thread ordering in <see cref="ConfluenceSpaceImporter"/>/
/// <see cref="ConfluenceImportValidator"/> (one tree per page) — the same "structural
/// agreement beats two implementations that are supposed to match" reasoning applies to
/// both: page creation and comment creation both require the parent to exist first
/// (<c>CreatePageAsync</c>/<c>AddCommentAsync</c> both validate this), so both need the
/// exact same ordering guarantee.
/// </summary>
internal static class ForestOrderer
{
    /// <summary>
    /// Orders <paramref name="items"/> parent-before-child. An item whose parent id is
    /// null, or isn't the id of any other item in the set, is treated as a root. Returns
    /// the ordered, reachable items plus any items unreachable from a root (a parent
    /// reference cycle) separately, rather than silently dropping them.
    /// </summary>
    public static (List<T> Ordered, List<T> Orphaned) OrderParentFirst<T, TId>(
        IReadOnlyList<T> items, Func<T, TId> getId, Func<T, TId?> getParentId)
        where TId : notnull
    {
        var byId = items.ToDictionary(getId);
        var childrenByParent = items
            .Where(i => getParentId(i) is { } parentId && byId.ContainsKey(parentId))
            .ToLookup(i => getParentId(i)!);

        var roots = items.Where(i => getParentId(i) is not { } parentId || !byId.ContainsKey(parentId)).ToList();

        var ordered = new List<T>(items.Count);
        var accountedFor = new HashSet<TId>();
        var queue = new Queue<T>(roots);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            ordered.Add(item);
            accountedFor.Add(getId(item));

            foreach (var child in childrenByParent[getId(item)])
            {
                queue.Enqueue(child);
            }
        }

        var orphaned = items.Where(i => !accountedFor.Contains(getId(i))).ToList();
        return (ordered, orphaned);
    }
}
