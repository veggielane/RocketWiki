namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.4:
/// <code>
/// canView(page) = spaceRole ≥ viewer AND every view-restriction on page + ancestors passes
/// canEdit(page) = canView(page) AND spaceRole ≥ editor AND every edit-restriction on page + ancestors passes
/// </code>
/// plus the replica invariant: canEdit is unconditionally false on a replica space.
/// </summary>
public sealed record EffectivePermission(bool CanView, bool CanEdit, string? ViewDenialReason, string? EditDenialReason);
