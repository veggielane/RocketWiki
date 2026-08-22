using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

public class RuleExpressionSerializerTests
{
    [Fact]
    public void Parse_Everyone_ReturnsEveryoneCondition()
    {
        var node = RuleExpressionSerializer.Parse("""{ "everyone": true }""");

        Assert.IsType<EveryoneCondition>(node);
    }

    [Fact]
    public void Parse_Group_ReturnsGroupCondition()
    {
        var node = RuleExpressionSerializer.Parse("""{ "group": "engineering" }""");

        var group = Assert.IsType<GroupCondition>(node);
        Assert.Equal("engineering", group.Group);
    }

    [Fact]
    public void Parse_User_ReturnsUserCondition()
    {
        var node = RuleExpressionSerializer.Parse("""{ "user": "sub-123" }""");

        var user = Assert.IsType<UserCondition>(node);
        Assert.Equal("sub-123", user.UserId);
    }

    [Fact]
    public void Parse_Attr_ReturnsAttrConditionWithInList()
    {
        var node = RuleExpressionSerializer.Parse("""{ "attr": "nationality", "in": ["NZ", "US"] }""");

        var attr = Assert.IsType<AttrCondition>(node);
        Assert.Equal("nationality", attr.Attribute);
        Assert.Equal(new[] { "NZ", "US" }, attr.In);
    }

    [Fact]
    public void Parse_NestedAllOfAnyOf_ExampleFromDesignDoc()
    {
        const string json = """
        {
          "allOf": [
            { "group": "engineering" },
            { "anyOf": [
              { "attr": "nationality", "in": ["NZ", "US"] },
              { "group": "export-cleared" }
            ]}
          ]
        }
        """;

        var node = RuleExpressionSerializer.Parse(json);

        var allOf = Assert.IsType<AllOfNode>(node);
        Assert.Equal(2, allOf.Children.Count);
        Assert.IsType<GroupCondition>(allOf.Children[0]);
        var anyOf = Assert.IsType<AnyOfNode>(allOf.Children[1]);
        Assert.Equal(2, anyOf.Children.Count);
    }

    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        // RuleNode records don't get deep collection equality for free (array vs
        // List<T> compare by reference), so round-trip is asserted by re-serializing
        // both sides to a canonical JSON string rather than via record Equals.
        var original = new AllOfNode(new RuleNode[]
        {
            new GroupCondition("engineering"),
            new AnyOfNode(new RuleNode[]
            {
                new AttrCondition("nationality", new[] { "NZ", "US" }),
                new GroupCondition("export-cleared"),
            }),
        });

        var json = RuleExpressionSerializer.Serialize(original);
        var roundTripped = RuleExpressionSerializer.Parse(json);
        var roundTrippedJson = RuleExpressionSerializer.Serialize(roundTripped);

        Assert.Equal(json, roundTrippedJson);
    }

    [Theory]
    [InlineData("not valid json {{{")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("[]")]
    // Unknown/unsupported condition types - there is no NOT and no deny rule (design.md §6.3).
    [InlineData("""{ "not": { "group": "engineering" } }""")]
    [InlineData("""{ "deny": { "group": "engineering" } }""")]
    // Wrong shapes for recognized keys.
    [InlineData("""{ "everyone": false }""")]
    [InlineData("""{ "everyone": "true" }""")]
    [InlineData("""{ "group": 123 }""")]
    [InlineData("""{ "group": "" }""")]
    [InlineData("""{ "user": "" }""")]
    [InlineData("""{ "attr": "nationality" }""")]
    [InlineData("""{ "in": ["NZ"] }""")]
    [InlineData("""{ "attr": "nationality", "in": [] }""")]
    [InlineData("""{ "attr": "nationality", "in": "NZ" }""")]
    [InlineData("""{ "attr": "nationality", "in": [1, 2] }""")]
    [InlineData("""{ "attr": 1, "in": ["NZ"] }""")]
    // Extra/combined keys that don't match a recognized shape exactly.
    [InlineData("""{ "group": "engineering", "user": "sub-1" }""")]
    [InlineData("""{ "allOf": [], "group": "engineering" }""")]
    // Empty combinators - rejected at parse time, not silently treated as vacuous truth.
    [InlineData("""{ "allOf": [] }""")]
    [InlineData("""{ "anyOf": [] }""")]
    [InlineData("""{ "allOf": "not-an-array" }""")]
    // Malformed nested node poisons the whole tree.
    [InlineData("""{ "allOf": [ { "group": "engineering" }, { "bogus": true } ] }""")]
    [InlineData("""{ "anyOf": [ { "everyone": true }, { "bogus": true } ] }""")]
    // A rule node must be a JSON object, not a scalar or array.
    [InlineData("""{ "allOf": ["engineering"] }""")]
    public void Parse_MalformedInput_Throws(string json)
    {
        Assert.Throws<MalformedRuleExpressionException>(() => RuleExpressionSerializer.Parse(json));
    }

    [Fact]
    public void TryParse_MalformedInput_ReturnsFalseWithError()
    {
        var ok = RuleExpressionSerializer.TryParse("""{ "bogus": true }""", out var node, out var error);

        Assert.False(ok);
        Assert.Null(node);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueWithNode()
    {
        var ok = RuleExpressionSerializer.TryParse("""{ "everyone": true }""", out var node, out var error);

        Assert.True(ok);
        Assert.NotNull(node);
        Assert.Null(error);
    }
}
