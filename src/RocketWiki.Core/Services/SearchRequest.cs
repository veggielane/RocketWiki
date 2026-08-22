namespace RocketWiki.Core.Services;

/// <summary>design.md §9.1: keyword search over Page.Title + Page.CurrentContent, with space and label facets. Labels facet matches ANY of the given names (OR), not all.</summary>
public sealed record SearchRequest(string Query, string? SpaceKey, IReadOnlyList<string>? Labels);

/// <summary>Snippet is a naive fixed-length excerpt of CurrentContent, not match-highlighted - a v1 simplification, not true relevance snippeting.</summary>
public sealed record SearchHit(Guid PageId, string Title, string SpaceKey, string Snippet);
