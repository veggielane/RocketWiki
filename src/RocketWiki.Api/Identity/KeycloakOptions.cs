using System.ComponentModel.DataAnnotations;

namespace RocketWiki.Api.Identity;

/// <summary>
/// The <c>Keycloak</c> configuration section (design.md §11): the realm, the optional
/// explicit authority, and the audience the JWT bearer handler requires. Bound in
/// Program.cs and <b>validated at startup</b>: a blank realm would derive an authority
/// ending in <c>/realms/</c>, a blank audience would accept no token at all, and an
/// authority that is not an absolute URL cannot fetch discovery metadata — each of them
/// a deployment that boots and then rejects every sign-in, which is exactly the failure
/// worth moving to boot time with the key named.
///
/// <para><b>One authority derivation for the whole process.</b> The JWT bearer handler and
/// the MCP protected-resource metadata used to each compute "explicit
/// <c>Keycloak:Authority</c>, else the Aspire-injected <c>keycloak</c> connection string
/// plus <c>/realms/{Realm}</c>" in their own code, with a comment asking that the two be
/// kept in step — because if they diverged, tokens would validate against one realm while
/// MCP discovery advertised another. Both now call <see cref="ResolveAuthority"/>; there
/// is nothing left to keep in step.</para>
///
/// <para>Consumed lazily, through <c>IOptions&lt;KeycloakOptions&gt;</c> inside the
/// options-configure callbacks of both consumers, never read off the builder: the bearer
/// options are resolved at first use and the MCP metadata at options-resolution time, so
/// a test host's configuration is the configuration in force. The connection string
/// itself stays on <c>GetConnectionString</c> (Aspire injects it; that is the idiomatic
/// API and it is not wrapped here).</para>
/// </summary>
public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>Realm name, composed onto the <c>keycloak</c> connection string when no
    /// explicit <see cref="Authority"/> is set.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Keycloak:Realm must be a non-empty realm name.")]
    public string Realm { get; set; } = "rocketwiki";

    /// <summary>Explicit OIDC authority (<c>https://keycloak.internal/realms/rocketwiki</c>).
    /// Set only when running standalone outside Aspire; under Aspire it is derived.
    /// A property-level attribute rather than <c>IValidatableObject</c>, deliberately:
    /// <c>Validator</c> skips object-level validation when any property fails, so a blank
    /// realm would have hidden a malformed authority from the startup report.</summary>
    [RegularExpression(@"^https?://[^\s/?#]+[^\s]*$", ErrorMessage = "Keycloak:Authority must be an absolute http(s) URL when set.")]
    public string? Authority { get; set; }

    /// <summary>The <c>aud</c> a bearer token must carry. The shipped appsettings.json sets
    /// <c>rocketwiki-api</c> to match the realm's audience mapper; this is the code
    /// fallback if appsettings is stripped.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Keycloak:Audience must be a non-empty audience.")]
    public string Audience { get; set; } = "rocketwiki";

    /// <summary>
    /// The one authority rule: <see cref="Authority"/> when set, else the Aspire-injected
    /// base URL plus <c>/realms/{Realm}</c>, else <c>null</c> — no authority, so bearer
    /// validation cannot fetch metadata and no authenticated request succeeds (fail closed
    /// by construction, design.md §15).
    /// </summary>
    public string? ResolveAuthority(string? keycloakConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(Authority))
        {
            return Authority;
        }

        return keycloakConnectionString is not null
            ? $"{keycloakConnectionString.TrimEnd('/')}/realms/{Realm}"
            : null;
    }
}
