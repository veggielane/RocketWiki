using System.Security.Claims;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §6.1/§21.15: the one claim-to-Principal mapping, pinned at the unit tier.
/// What it must do is small — the three fixed attributes plus every configured selector
/// claim — and what it must NOT do is the part worth a test: a claim nobody configured
/// never becomes a principal attribute, because an attribute that exists is an attribute
/// a rule or a gate can match on.
/// </summary>
public class PrincipalBuilderTests
{
    private static readonly SelectorCatalog Catalog = SelectorCatalog.Create(
    [
        new SelectorCategory("FRUIT", null, "fruit", ["APPLE", "BANANA"]),
        new SelectorCategory("REGION", null, null, ["NORTH", "SOUTH"]),
    ]);

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    [Fact]
    public void PrincipalBuilder_MapsOnlyConfiguredSelectorClaims()
    {
        var builder = new PrincipalBuilder(Catalog);
        var principal = builder.Build(Authenticated(
            ("sub", "alice"),
            ("groups", "engineering"),
            ("nationality", "NZ"),
            ("clearance", "SECRET"),
            ("fruit", " Yes "),
            // Looks exactly like a selector claim, but no configured category names it.
            ("vegetable", "yes"),
            // An ordinary token claim, present to prove the builder maps by allowlist,
            // not by "everything that is there".
            ("email", "alice@example.test")));

        Assert.NotNull(principal);
        Assert.Equal("alice", principal!.UserId);
        Assert.Equal(["engineering"], principal.Groups);

        Assert.Equal(
            ["clearance", "fruit", "nationality"],
            principal.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // Values travel as-is: the trim-and-case rule for `yes` belongs to SelectorGate
        // (design.md §21.15), stated once, not re-implemented in the builder.
        Assert.Equal([" Yes "], principal.Attributes["fruit"]);
        Assert.True(SelectorGate.IsEligible(principal, Catalog.Categories[0]));
        Assert.False(principal.Attributes.ContainsKey("vegetable"));
        Assert.False(principal.Attributes.ContainsKey("email"));
    }

    [Fact]
    public void AnAbsentSelectorClaim_IsAbsentFromTheAttributes_NotAnEmptyList()
    {
        // Principal's fail-closed contract: a key nobody holds a value for is missing,
        // which SelectorGate reads as "not eligible" without any empty-string comparison.
        var principal = new PrincipalBuilder(Catalog).Build(Authenticated(("sub", "bob")));

        Assert.NotNull(principal);
        Assert.False(principal!.Attributes.ContainsKey("fruit"));
        Assert.False(SelectorGate.IsEligible(principal, Catalog.Categories[0]));
        // The claim-less category admits everyone regardless.
        Assert.True(SelectorGate.IsEligible(principal, Catalog.Categories[1]));
    }

    [Fact]
    public void AnEmptyCatalog_MapsNoSelectorClaims_AndStillMapsTheFixedThree()
    {
        var principal = new PrincipalBuilder(SelectorCatalog.Empty).Build(Authenticated(
            ("sub", "carol"), ("nationality", "UK"), ("clearance", "OFFICIAL"), ("fruit", "yes")));

        Assert.NotNull(principal);
        Assert.Equal(
            ["clearance", "nationality"],
            principal!.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void UnauthenticatedOrSubjectless_IsNull()
    {
        var builder = new PrincipalBuilder(Catalog);

        Assert.Null(builder.Build(null));
        Assert.Null(builder.Build(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(builder.Build(Authenticated(("groups", "engineering"))));
    }
}
