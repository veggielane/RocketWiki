namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.4:
/// <code>
/// canView(page) = space access AND marking gate AND every view-restriction on page + ancestors passes
/// canEdit(page) = canView(page) AND role ≥ editor AND every edit-restriction on page + ancestors passes
/// </code>
/// plus the replica invariant: canEdit is unconditionally false on a replica space.
/// Space access is an access grant, a role is a role grant, and the two never stand in
/// for each other.
/// </summary>
public sealed record EffectivePermission(bool CanView, bool CanEdit, string? ViewDenialReason, string? EditDenialReason);
