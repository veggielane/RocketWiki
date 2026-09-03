using System.Text.Json;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The mirror-to-Principal mapping the profile page reads through (design.md §6.2), pinned
/// at the unit tier beside <see cref="PrincipalBuilderTests"/>: the same allowlist, the
/// same "absent, not empty" rule, and fail-closed on anything the mirror cannot say. The
/// end-to-end agreement with the gates is <c>UserProfileQueryTests.Profile_AgreesWithTheGate</c>;
/// this file is the parser's own contract.
/// </summary>
public class MirroredPrincipalTests
{
    private static readonly SelectorCatalog Catalog = SelectorCatalog.Create(
    [
        new SelectorCategory("FRUIT", null, "fruit", ["APPLE", "BANANA"]),
        new SelectorCategory("REGION", null, null, ["NORTH", "SOUTH"]),
    ]);

    private static string Mirror(Dictionary<string, string[]> attributes) => JsonSerializer.Serialize(attributes);

    [Fact]
    public void MapsTheSameAllowlistAsThePrincipalBuilder_AndNothingElse()
    {
        var principal = MirroredPrincipal.Build("alice", Mirror(new()
        {
            ["nationality"] = ["NZ", "GB"],
            ["clearance"] = ["SECRET"],
            ["fruit"] = [" Yes "],
            // A mirror written by a build that recorded more than it should have, or a
            // hand edit: neither becomes an attribute a gate could read.
            ["vegetable"] = ["yes"],
            ["email"] = ["alice@example.test"],
            ["groups"] = ["engineering"],
        }), Catalog);

        Assert.Equal("alice", principal.UserId);
        Assert.Empty(principal.Groups);
        Assert.Equal(
            ["clearance", "fruit", "nationality"],
            principal.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // Raw values: the gate decides what " Yes " is worth, exactly as for the token.
        Assert.Equal([" Yes "], principal.Attributes["fruit"]);
        Assert.True(SelectorGate.IsEligible(principal, Catalog.Categories[0]));
        Assert.Equal(ClassificationLevel.Secret, ClearanceGate.ResolveClearance(principal));
    }

    [Fact]
    public void AnEmptyList_IsAbsentFromTheAttributes_NotPresentAndEmpty()
    {
        // JIT provisioning writes every configured key, empty when the token had no such
        // claim. The principal must read that as "no value" - the fail-closed contract
        // ClearanceGate ("absent means the floor") and SelectorGate ("absent is not
        // eligible") are written against.
        var principal = MirroredPrincipal.Build("bob", Mirror(new()
        {
            ["nationality"] = [],
            ["clearance"] = [],
            ["fruit"] = [],
        }), Catalog);

        Assert.Empty(principal.Attributes);
        Assert.Equal(ClearanceGate.DefaultClearance, ClearanceGate.ResolveClearance(principal));
        Assert.False(SelectorGate.IsEligible(principal, Catalog.Categories[0]));
        Assert.True(SelectorGate.IsEligible(principal, Catalog.Categories[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("[\"a\",\"b\"]")]
    [InlineData("{\"clearance\":\"SECRET\"}")] // a string where a list is expected
    [InlineData("{\"clearance\":null,\"fruit\":[null]}")]
    public void AMirrorThatCannotBeRead_YieldsNoAttributes_FailingClosed(string? attributesJson)
    {
        var principal = MirroredPrincipal.Build("carol", attributesJson, Catalog);

        Assert.Empty(principal.Attributes);
        Assert.Equal(ClearanceGate.DefaultClearance, ClearanceGate.ResolveClearance(principal));
        Assert.False(SelectorGate.IsEligible(principal, Catalog.Categories[0]));
    }

    [Fact]
    public void ACategoryNoLongerConfigured_IsNotMapped_AndOneAddedSince_ReadsAsNoClaim()
    {
        var mirror = Mirror(new() { ["fruit"] = ["yes"], ["clearance"] = ["TOP_SECRET"] });

        // Catalog shrank since the sign-in: the recorded fruit claim gates nothing now.
        var underEmpty = MirroredPrincipal.Build("dave", mirror, SelectorCatalog.Empty);
        Assert.Equal(["clearance"], underEmpty.Attributes.Keys);

        // Catalog grew since the sign-in: the new category's claim was never recorded,
        // which the gate reads as not eligible - never as "unknown, so assume yes".
        var grown = SelectorCatalog.Create(
        [
            .. Catalog.Categories,
            new SelectorCategory("VEGETABLE", null, "vegetable", ["CARROT"]),
        ]);
        var underGrown = MirroredPrincipal.Build("dave", mirror, grown);
        Assert.False(underGrown.Attributes.ContainsKey("vegetable"));
        Assert.Equal(
            ["FRUIT", "REGION"],
            SelectorGate.ResolveEligibleCategories(underGrown, grown).OrderBy(n => n, StringComparer.Ordinal));
    }
}
