namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md: User — local mirror for display only. design.md §6.1: authorization
/// never reads this table; the rule engine evaluates a Principal built from the token.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>OIDC `sub` claim; null for shadow users created by sync.</summary>
    public string? Subject { get; set; }

    public string? Email { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Mirrored registered attributes (nationality, clearance) plus every configured
    /// selector claim, as raw claim values keyed by claim name (design.md §11.3).
    /// Nationality is admin-visible only; clearance and selector eligibility are shown
    /// to every signed-in user on the profile page (design.md §6.2), derived at read time
    /// through the gates. Never an authorization input (design.md §6.1).
    /// </summary>
    public string AttributesJson { get; set; } = "{}";

    /// <summary>Shadow user (sync author), never loginable.</summary>
    public bool IsExternal { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
