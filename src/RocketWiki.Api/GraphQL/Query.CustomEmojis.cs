using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// The custom-emoji registry, for any authenticated user — this is what feeds the
    /// SPA's picker/autocomplete and, crucially, the renderer's known-names set: a
    /// <c>:name:</c> in content is an emoji only when the name appears here, and
    /// literal text otherwise (the whole grammar/replica story — see EmojiName).
    /// Anonymous callers get an empty list, the same absent-shaped answer every other
    /// read gives them.
    ///
    /// <c>etag</c> is each entry's serve-route ETag (the stored bytes' hash), so the
    /// SPA can key its per-name image cache and detect a delete+recreate without
    /// refetching bytes. A separate registry-version root field was considered and
    /// rejected: this list of short names IS as cheap as any version probe, so a
    /// version field would add a second cache artifact without saving a round trip.
    /// </summary>
    [NoAudit("Display-asset vocabulary (names + cache tags), readable in full by every authenticated user; no wiki content and no per-subject access decision - same reasoning that leaves emoji/avatar GETs and display-name resolution unaudited (design.md §7).")]
    public async Task<IReadOnlyList<CustomEmojiRef>> CustomEmojis(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        if (principalAccessor.Current is null)
        {
            return [];
        }

        var emojis = await db.CustomEmojis
            .OrderBy(e => e.Name)
            .Select(e => new { e.Name, e.ContentHash })
            .ToListAsync(cancellationToken);

        return emojis
            .Select(e => new CustomEmojiRef(e.Name, $"\"{Convert.ToHexStringLower(e.ContentHash)}\""))
            .ToList();
    }
}

/// <summary>One registry entry as the SPA needs it: the <c>:name:</c> key and the
/// serve route's strong ETag for that name's current image (its cache key).</summary>
public sealed record CustomEmojiRef(string Name, string Etag);
