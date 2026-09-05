using System.ComponentModel.DataAnnotations;
using RocketWiki.Api.Identity;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// <see cref="KeycloakOptions.ResolveAuthority"/> is the one authority derivation both
/// the JWT bearer handler (Program.cs) and the MCP protected-resource metadata
/// (McpServerConfiguration) call. It used to exist twice, with a comment asking that the
/// copies be kept in step; these tests pin the single rule so a change to it is a change
/// to both consumers, visibly.
/// </summary>
public sealed class KeycloakOptionsTests
{
    [Fact]
    public void ExplicitAuthority_WinsOverTheConnectionString()
    {
        var options = new KeycloakOptions { Authority = "https://keycloak.internal/realms/prod", Realm = "ignored" };

        Assert.Equal("https://keycloak.internal/realms/prod", options.ResolveAuthority("http://keycloak:8080"));
    }

    [Fact]
    public void ConnectionString_PlusRealm_ComposesTheAuthority_TrimmingATrailingSlash()
    {
        var options = new KeycloakOptions { Realm = "rocketwiki" };

        Assert.Equal("http://keycloak:8080/realms/rocketwiki", options.ResolveAuthority("http://keycloak:8080/"));
        Assert.Equal("http://keycloak:8080/realms/rocketwiki", options.ResolveAuthority("http://keycloak:8080"));
    }

    /// <summary>Neither source: no authority. The bearer handler then has no metadata to
    /// validate against and every authenticated request fails — fail closed (design.md
    /// §15), never a guessed host.</summary>
    [Fact]
    public void NeitherSource_YieldsNull()
    {
        Assert.Null(new KeycloakOptions().ResolveAuthority(null));
    }

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(Validate(new KeycloakOptions()));
    }

    [Theory]
    [InlineData("keycloak.internal/realms/x")]
    [InlineData("ftp://keycloak.internal")]
    [InlineData("not a url")]
    public void ANonHttpAuthority_FailsValidation(string authority)
    {
        var failures = Validate(new KeycloakOptions { Authority = authority });

        Assert.Contains(failures, f => f.ErrorMessage!.Contains("Keycloak:Authority", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlankRealmOrAudience_FailsValidation()
    {
        var failures = Validate(new KeycloakOptions { Realm = "", Audience = "" });

        Assert.Contains(failures, f => f.ErrorMessage!.Contains("Keycloak:Realm", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.ErrorMessage!.Contains("Keycloak:Audience", StringComparison.Ordinal));
    }

    /// <summary>The same DataAnnotations + IValidatableObject pass ValidateDataAnnotations runs at startup.</summary>
    private static List<ValidationResult> Validate(KeycloakOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
