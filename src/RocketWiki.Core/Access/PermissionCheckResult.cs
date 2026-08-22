namespace RocketWiki.Core.Access;

/// <summary>Outcome of checking a set of restrictions, with the failing reason for audit (design.md §7).</summary>
public readonly record struct PermissionCheckResult(bool IsAllowed, string? DenialReason)
{
    public static PermissionCheckResult Allow() => new(true, null);

    public static PermissionCheckResult Deny(string reason) => new(false, reason);
}
