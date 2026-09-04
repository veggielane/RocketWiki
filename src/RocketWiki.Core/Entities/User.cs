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
    /// Mirrored claims, as raw claim values keyed by claim name (design.md §11.3):
    /// <c>nationality</c> (the registered attribute, admin-visible only on the roster)
    /// and <c>groups</c> (the group memberships, shown to every signed-in user on the
    /// profile page, design.md §6.2). It used to mirror a clearance claim and every
    /// configured selector claim as well; both went with the gates that read them, since
    /// this deployment carries neither attribute in Keycloak. Never an authorization
    /// input (design.md §6.1): the Principal is built from the token, every request.
    /// </summary>
    public string AttributesJson { get; set; } = "{}";

    /// <summary>Shadow user (sync author), never loginable.</summary>
    public bool IsExternal { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
