using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RocketWiki.Api.Identity;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §6.5's instance-admin role, read off the token.
///
/// These exist because the first real Keycloak token this project ever saw exposed a
/// bug nothing else could: <c>roles</c> is in
/// <c>JsonWebTokenHandler.DefaultInboundClaimTypeMap</c>, so the JWT bearer handler
/// renames it to <see cref="ClaimTypes.Role"/> and the accessor — which read only
/// <c>"roles"</c> — matched nobody. Every admin gate refused every caller, and
/// <c>createSpace</c> told a user holding the realm's <c>admin</c> role "instance admin
/// required". The integration tier could not catch it: <c>TestAuthHandler</c> builds its
/// ClaimsPrincipal by hand and does no inbound mapping, so a test that sets
/// <c>"roles"</c> reads back <c>"roles"</c> and the mapped shape never occurs.
///
/// The mapped case is therefore the one that matters most here — it is the shape a real
/// deployment actually produces.
/// </summary>
public class InstanceRoleAccessorTests
{
    [Theory]
    // Unmapped: MapInboundClaims disabled, or any non-JWT scheme (the test tier).
    [InlineData("roles", "admin", true)]
    // Mapped: the DEFAULT for JwtBearer, and what real Keycloak tokens arrive as.
    [InlineData(ClaimTypes.Role, "admin", true)]
    [InlineData("roles", "user", false)]
    [InlineData(ClaimTypes.Role, "user", false)]
    public void RecognizesTheAdminRoleUnderEitherClaimName(string claimType, string value, bool expected)
    {
        var accessor = AccessorFor(new Claim(claimType, value));

        Assert.Equal(expected, accessor.IsInstanceAdmin);
    }

    [Fact]
    public void FindsAdminAlongsideOtherRoles()
    {
        // Keycloak's default-roles composite means "admin" never arrives alone.
        var accessor = AccessorFor(
            new Claim(ClaimTypes.Role, "user"),
            new Claim(ClaimTypes.Role, "admin"));

        Assert.True(accessor.IsInstanceAdmin);
    }

    [Fact]
    public void AnonymousIsNotAnAdmin()
    {
        var accessor = new InstanceRoleAccessor(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        Assert.False(accessor.IsInstanceAdmin);
    }

    [Fact]
    public void NoHttpContextIsNotAnAdmin()
    {
        // Fails closed rather than throwing — the accessor is resolvable from scopes
        // that have no request (see PrincipalBuilder's doc on the same trap).
        var accessor = new InstanceRoleAccessor(new HttpContextAccessor { HttpContext = null });

        Assert.False(accessor.IsInstanceAdmin);
    }

    private static InstanceRoleAccessor AccessorFor(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };

        return new InstanceRoleAccessor(new HttpContextAccessor { HttpContext = context });
    }
}
