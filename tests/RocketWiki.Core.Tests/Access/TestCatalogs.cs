using RocketWiki.Core.Access;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// The one selector catalog every test tier uses (design.md §21.15), so a scenario reads
/// the same in a Core unit test, a SQLite service test, an API fixture and the dev realm:
/// <c>FRUIT</c> (<c>APPLE</c>, <c>BANANA</c>) gated by the <c>fruit</c> claim, and
/// <c>REGION</c> (<c>NORTH</c>, <c>SOUTH</c>) gated by nobody. Linked into the other test
/// projects as a shared source file rather than copied, so the vocabulary cannot drift
/// between tiers.
/// </summary>
public static class TestCatalogs
{
    public const string FruitClaim = "fruit";

    public static readonly SelectorValue Apple = new("FRUIT", "APPLE");
    public static readonly SelectorValue Banana = new("FRUIT", "BANANA");
    public static readonly SelectorValue North = new("REGION", "NORTH");
    public static readonly SelectorValue South = new("REGION", "SOUTH");

    public static SelectorCatalog Fruit { get; } = SelectorCatalog.Create(
    [
        new SelectorCategory("FRUIT", "Fruit compartments", FruitClaim, ["APPLE", "BANANA"]),
        new SelectorCategory("REGION", "Regional releasability", null, ["NORTH", "SOUTH"]),
    ]);
}
