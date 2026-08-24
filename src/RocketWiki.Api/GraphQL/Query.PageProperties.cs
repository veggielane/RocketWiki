using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// The instance's page-property key registry (design.md §20), for any authenticated
    /// user — this is what the properties screen's key picker binds to. Ordered by
    /// <c>sortOrder</c> then <c>key</c>, the same order <c>Page.properties</c> uses, so
    /// the picker and the page agree without either side re-sorting.
    ///
    /// Readable by every signed-in user, like <c>customEmojis</c>: this is vocabulary,
    /// not content. A key's existence says nothing about which pages use it (that
    /// question has no query surface yet, and would have to be permission-filtered per
    /// page when it gets one). Anonymous callers get an empty list, the same
    /// absent-shaped answer every other read gives them.
    /// </summary>
    [NoAudit("Instance vocabulary (key names + display order), readable in full by every authenticated user; no wiki content and no per-subject access decision - the same reasoning that leaves the customEmojis registry listing unaudited (design.md §7/§20).")]
    public async Task<IReadOnlyList<PagePropertyKeyRef>> PagePropertyKeys(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        if (principalAccessor.Current is null)
        {
            return [];
        }

        var keys = await db.PagePropertyKeys
            .Select(k => new PagePropertyKeyRef(k.Id, k.Key, k.Description, k.SortOrder))
            .ToListAsync(cancellationToken);

        // Ordered in memory, ordinally, for the same reason the properties DataLoader
        // does: a database ORDER BY on a text column sorts by the provider's collation,
        // and SQL Server's and SQLite's disagree.
        return keys
            .OrderBy(k => k.SortOrder)
            .ThenBy(k => k.Key, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>One registry entry as the properties screen needs it. Not the
/// <c>PagePropertyKey</c> entity: that carries <c>CreatedBy</c>/<c>PageProperties</c>
/// navigations, and exposing it would put a User- and PageProperty-shaped route into
/// the schema for a list that is meant to be four scalar fields.</summary>
public sealed record PagePropertyKeyRef(Guid Id, string Key, string? Description, int SortOrder);
