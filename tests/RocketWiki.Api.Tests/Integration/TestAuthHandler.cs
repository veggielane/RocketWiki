using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Stands in for real Keycloak JWT bearer auth (design.md §11) in integration
/// tests, since there's no Keycloak to issue tokens against here (design.md
/// §14's SQLite tier is deliberately container-free). Registered as the
/// default authentication scheme by <see cref="RocketWikiApiFactory"/>,
/// replacing "Bearer" — no real JwtBearer code runs in this tier at all.
///
/// A request with no <see cref="ClaimsHeaderName"/> header authenticates as
/// anonymous, exactly like a request with no bearer token would — so
/// <c>Query.Me</c>'s anonymous path is exercised by simply not calling
/// <see cref="TestUserHttpClientExtensions.SetTestUser"/>.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string ClaimsHeaderName = "X-Test-Claims";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ClaimsHeaderName, out var header) || header.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(header.ToString()));
        var claims = JsonSerializer.Deserialize<TestClaim[]>(json) ?? [];

        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    internal sealed record TestClaim(string Type, string Value);
}
