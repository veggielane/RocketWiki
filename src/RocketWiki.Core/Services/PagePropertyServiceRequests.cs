namespace RocketWiki.Core.Services;

/// <summary>Upsert: one value per key per page, so setting an existing key overwrites it.</summary>
public sealed record SetPagePropertyRequest(Guid PageId, Guid PagePropertyKeyId, string Value);

public sealed record RemovePagePropertyRequest(Guid PageId, Guid PagePropertyKeyId);

/// <summary><paramref name="Key"/> is the display form; the service derives the
/// normalized form uniqueness is enforced on (see <c>PagePropertyKey.Normalize</c>).</summary>
public sealed record CreatePagePropertyKeyRequest(string Key, string? Description);

/// <summary>
/// One page property, flattened for callers: the registry key's id and display name
/// beside the page's value, plus the key's <paramref name="SortOrder"/> so every
/// consumer renders the same order without re-reading the registry.
///
/// Deliberately not the <c>PageProperty</c> entity. That carries a <c>Page</c>
/// navigation, and returning it from a read path (or a mutation payload) would open a
/// Page-shaped route around the object-level authorization every such field must go
/// through — the same reasoning that produced <c>LabelRef</c>.
/// </summary>
public sealed record PagePropertyValue(Guid KeyId, string Key, string Value, int SortOrder);
