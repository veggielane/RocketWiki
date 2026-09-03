using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.15: the configured selector vocabulary is validated ONCE, at startup, and
/// every rule that makes a category name safe to put into a denial reason lives in that
/// validation. These pin each refusal, the canonical form, and the two lookups the gate
/// and the formatter depend on.
/// </summary>
public class SelectorCatalogTests
{
    private static SelectorCategory Category(string name, string? claim, params string[] values) =>
        new(name, null, claim, values);

    [Fact]
    public void Create_CanonicalizesNamesAndValues()
    {
        var catalog = SelectorCatalog.Create([Category(" fruit ", " fruit ", "apple", " Banana ")]);

        var category = Assert.Single(catalog.Categories);
        Assert.Equal("FRUIT", category.Name);
        Assert.Equal(["APPLE", "BANANA"], category.Values);
        // The claim name is trimmed but NOT upper-cased: it is an attribute key matched
        // ordinally against the principal (§6.3), not a marking token.
        Assert.Equal("fruit", category.ClaimName);
        Assert.True(catalog.IsKnown(new SelectorValue("fruit", "apple")));
    }

    [Fact]
    public void DuplicateCategoryName_IsRefused()
    {
        var error = Assert.Throws<SelectorCatalogException>(() => SelectorCatalog.Create(
        [
            Category("FRUIT", null, "APPLE"),
            Category("fruit", null, "PEAR"), // same name after canonicalization
        ]));

        Assert.Contains("FRUIT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateValueWithinCategory_IsRefused()
    {
        Assert.False(SelectorCatalog.TryCreate([Category("FRUIT", null, "APPLE", "apple")], out _, out var error));
        Assert.Contains("APPLE", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyValues_IsRefused()
    {
        Assert.False(SelectorCatalog.TryCreate([Category("FRUIT", null)], out _, out var error));
        Assert.Contains("FRUIT", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("sub")]
    [InlineData("clearance")]
    [InlineData("nationality")]
    [InlineData("Clearance")]   // refused case-insensitively: a claim that LOOKS like the reserved one is the trap
    public void ReservedClaimName_IsRefused(string claim)
    {
        // A category gated by "clearance" would test the clearance values for "yes" -
        // never eligible, silently. Refused at startup rather than reasoned about at runtime.
        Assert.False(SelectorCatalog.TryCreate([Category("FRUIT", claim, "APPLE")], out _, out var error));
        Assert.Contains(claim, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FRU IT")]
    [InlineData("FRUIT/VEG")]
    [InlineData("FRÜIT")]
    [InlineData("")]
    [InlineData("   ")]
    public void NameWithWhitespaceOrSlash_IsRefused(string name)
    {
        // A category name is a label token (space-separated) and the caveat uses "/" -
        // neither may appear inside a name.
        Assert.False(SelectorCatalog.TryCreate([Category(name, null, "APPLE")], out _, out _));
    }

    [Theory]
    [InlineData("AP PLE")]
    [InlineData("AP/PLE")]
    [InlineData("")]
    public void ValueWithWhitespaceOrSlash_IsRefused(string value)
    {
        Assert.False(SelectorCatalog.TryCreate([Category("FRUIT", null, value)], out _, out _));
    }

    [Fact]
    public void OverLongNameOrValue_IsRefused_AtTheColumnLength()
    {
        // The column is nvarchar(32) and SQLite does not enforce lengths, so the catalog
        // is the tier-parity guard for configured tokens.
        var atLimit = new string('A', SelectorCatalog.MaxNameLength);
        var overLimit = new string('A', SelectorCatalog.MaxNameLength + 1);

        Assert.True(SelectorCatalog.TryCreate([Category(atLimit, null, atLimit)], out _, out _));
        Assert.False(SelectorCatalog.TryCreate([Category(overLimit, null, "APPLE")], out _, out _));
        Assert.False(SelectorCatalog.TryCreate([Category("FRUIT", null, overLimit)], out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankClaimName_MeansEveryoneEligible(string? claim)
    {
        var catalog = SelectorCatalog.Create([Category("REGION", claim, "NORTH")]);

        var category = Assert.Single(catalog.Categories);
        Assert.Null(category.ClaimName);
        Assert.False(category.RequiresClaim);
        Assert.Empty(catalog.ClaimNames);
        Assert.True(SelectorGate.IsEligible(Principal.Create("anyone", []), category));
    }

    [Fact]
    public void ClaimNames_AreDistinct_OneClaimMayGateSeveralCategories()
    {
        var catalog = SelectorCatalog.Create(
        [
            Category("FRUIT", "fruit", "APPLE"),
            Category("VEG", "fruit", "CARROT"),
            Category("REGION", null, "NORTH"),
        ]);

        Assert.Equal(["fruit"], catalog.ClaimNames);
    }

    [Fact]
    public void DisplayIndex_FollowsConfiguredOrder_UnknownIsLast()
    {
        var catalog = TestCatalogs.Fruit;

        Assert.Equal(0, catalog.DisplayIndex("FRUIT"));
        Assert.Equal(1, catalog.DisplayIndex("REGION"));
        Assert.Equal(int.MaxValue, catalog.DisplayIndex("COLOUR"));
        Assert.Equal(["FRUIT", "REGION"], catalog.Categories.Select(c => c.Name));
    }

    [Fact]
    public void IsKnown_RequiresBothTheCategoryAndTheValue()
    {
        var catalog = TestCatalogs.Fruit;

        Assert.True(catalog.IsKnown(TestCatalogs.Apple));
        Assert.False(catalog.IsKnown(new SelectorValue("FRUIT", "PEAR")));     // known category, unknown value
        Assert.False(catalog.IsKnown(new SelectorValue("COLOUR", "APPLE")));   // unknown category
        Assert.True(catalog.TryGet("FRUIT", out var fruit));
        Assert.Equal("fruit", fruit.ClaimName);
        Assert.False(catalog.TryGet("COLOUR", out _));
    }

    [Fact]
    public void Empty_KnowsNothing()
    {
        // The fail-closed catalog: an instance with no configuration knows no selector,
        // so any selector-bearing page is readable by nobody (§12's "unknown matches nobody").
        Assert.Empty(SelectorCatalog.Empty.Categories);
        Assert.Empty(SelectorCatalog.Empty.ClaimNames);
        Assert.False(SelectorCatalog.Empty.IsKnown(TestCatalogs.Apple));
        Assert.False(SelectorCatalog.Empty.TryGet("FRUIT", out _));
        Assert.Equal(int.MaxValue, SelectorCatalog.Empty.DisplayIndex("FRUIT"));
    }

    [Fact]
    public void Create_OnAnEmptyDefinitionList_IsEquivalentToEmpty()
    {
        var catalog = SelectorCatalog.Create([]);

        Assert.Empty(catalog.Categories);
        Assert.False(catalog.IsKnown(TestCatalogs.Apple));
    }

    [Theory]
    [InlineData("APPLE", true)]
    [InlineData("APPLE-2", true)]
    [InlineData("APPLE_2", true)]
    [InlineData("apple", false)]   // the grammar is applied to the CANONICAL form; lower-case never reaches it
    [InlineData("APPLE 2", false)]
    [InlineData("A/B", false)]
    [InlineData("", false)]
    public void IsWellFormedToken_IsTheClosedGrammar_TheImporterReuses(string token, bool expected) =>
        Assert.Equal(expected, SelectorCatalog.IsWellFormedToken(token));
}
