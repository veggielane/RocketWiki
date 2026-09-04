using System.Security.Claims;
using RocketWiki.Api.Identity;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §6.1: the one claim-to-Principal mapping, pinned at the unit tier. What it
/// must do is small — <c>groups</c> and <c>nationality</c>, and nothing else — and what
/// it must NOT do is the part worth a test: a claim not on that list never becomes a
/// principal attribute, because an attribute that exists is an attribute a rule or a gate
/// can match on. The list used to be longer (a clearance claim, and every configured
/// selector claim); both went with the gates that read them, and this is the test that
/// keeps them from creeping back in.
/// </summary>
public class PrincipalBuilderTests
{
    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    [Fact]
    public void PrincipalBuilder_MapsGroupsAndNationality_AndNothingElse()
    {
        var principal = PrincipalBuilder.Build(Authenticated(
            ("sub", "alice"),
            ("groups", "engineering"),
            ("groups", "legal"),
            ("nationality", "NZ"),
            ("nationality", "UK"),
            // The two claims the removed gates used to read. Neither is an attribute now:
            // a rule written against `clearance` would match nobody, which is the point.
            ("clearance", "SECRET"),
            ("fruit", "yes"),
            // Looks exactly like a selector claim; nothing configures it either way.
            ("vegetable", "yes"),
            // Ordinary token claims, present to prove the builder maps by allowlist, not
            // by "everything that is there".
            ("email", "alice@example.test"),
            ("roles", "admin")));

        Assert.NotNull(principal);
        Assert.Equal("alice", principal!.UserId);
        Assert.Equal(["engineering", "legal"], principal.Groups.OrderBy(g => g, StringComparer.Ordinal));

        var only = Assert.Single(principal.Attributes);
        Assert.Equal("nationality", only.Key);
        // Values travel as-is: canonicalization belongs to CaveatGate, stated once.
        Assert.Equal(["NZ", "UK"], only.Value);
    }

    [Theory]
    [InlineData("clearance")]
    [InlineData("fruit")]
    [InlineData("vegetable")]
    [InlineData("email")]
    [InlineData("sub")]
    public void AClaimOffTheList_IsNeverAnAttribute(string claimName)
    {
        var principal = PrincipalBuilder.Build(Authenticated(("sub", "bob"), (claimName, "anything")));

        Assert.NotNull(principal);
        Assert.False(principal!.Attributes.ContainsKey(claimName));
    }

    [Fact]
    public void AnAbsentNationalityClaim_IsAbsentFromTheAttributes_NotAnEmptyList()
    {
        // Principal's fail-closed contract: a key nobody holds a value for is missing,
        // which CaveatGate reads as "holds nothing" without any empty-string comparison.
        var principal = PrincipalBuilder.Build(Authenticated(("sub", "bob"), ("groups", "engineering")));

        Assert.NotNull(principal);
        Assert.Empty(principal!.Attributes);
        Assert.Equal(["engineering"], principal.Groups);
    }

    [Fact]
    public void UnauthenticatedOrSubjectless_IsNull()
    {
        Assert.Null(PrincipalBuilder.Build(null));
        Assert.Null(PrincipalBuilder.Build(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(PrincipalBuilder.Build(Authenticated(("groups", "engineering"))));
    }
}
