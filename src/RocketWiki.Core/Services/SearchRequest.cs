namespace RocketWiki.Core.Services;

/// <summary>design.md §9.1: keyword search over Page.Title + Page.CurrentContent, with space and label facets. Labels facet matches ANY of the given names (OR), not all.</summary>
public sealed record SearchRequest(string Query, string? SpaceKey, IReadOnlyList<string>? Labels);

/// <summary>
/// One permission-filtered search hit (design.md §9). Snippet is a plain-text,
/// whitespace-collapsed excerpt centered on the first literal term match (leading
/// excerpt when the match was title-only or stem-only) — built by
/// <see cref="Search.SnippetBuilder"/> strictly from content the caller passed
/// canView for, never markup, never highlight tags. HeadingPath is the breadcrumb
/// of the section containing the match (empty when the match isn't inside any
/// section), and AnchorId is that section's deep-link id per the cross-language
/// anchor contract (<see cref="Search.HeadingAnchors"/>; empty when HeadingPath is).
/// </summary>
public sealed record SearchHit(
    Guid PageId,
    string Title,
    string SpaceKey,
    string Snippet,
    IReadOnlyList<string> HeadingPath,
    string AnchorId);
