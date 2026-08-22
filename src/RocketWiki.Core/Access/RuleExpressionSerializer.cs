using System.Text.Json;
using System.Text.Json.Nodes;

namespace RocketWiki.Core.Access;

/// <summary>
/// Parses and serializes the rule expression JSON shape from design.md §6.3.
/// Validation is holistic: a document parses only if every node in the tree is one of
/// the recognized shapes below with exactly its expected keys. Any unrecognized key
/// set, wrong value kind, or empty combinator array fails the whole parse — there is
/// no notion of a "partially valid" tree, matching data-model.md's description of
/// ExpressionJson as "a validated AND/OR expression tree".
/// </summary>
public static class RuleExpressionSerializer
{
    public static RuleNode Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new MalformedRuleExpressionException("Expression JSON is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MalformedRuleExpressionException($"Invalid JSON: {ex.Message}");
        }

        using (document)
        {
            return ParseNode(document.RootElement);
        }
    }

    public static bool TryParse(string json, out RuleNode? node, out string? error)
    {
        try
        {
            node = Parse(json);
            error = null;
            return true;
        }
        catch (MalformedRuleExpressionException ex)
        {
            node = null;
            error = ex.Message;
            return false;
        }
    }

    public static string Serialize(RuleNode node) => ToJsonNode(node).ToJsonString();

    private static RuleNode ParseNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new MalformedRuleExpressionException(
                $"Expected a JSON object for a rule node, found {element.ValueKind}.");
        }

        var properties = element.EnumerateObject().ToList();
        var keys = new HashSet<string>(properties.Select(p => p.Name), StringComparer.Ordinal);

        if (keys.SetEquals(new[] { "allOf" }))
        {
            return new AllOfNode(ParseChildren(element.GetProperty("allOf"), "allOf"));
        }

        if (keys.SetEquals(new[] { "anyOf" }))
        {
            return new AnyOfNode(ParseChildren(element.GetProperty("anyOf"), "anyOf"));
        }

        if (keys.SetEquals(new[] { "everyone" }))
        {
            var value = element.GetProperty("everyone");
            if (value.ValueKind != JsonValueKind.True)
            {
                throw new MalformedRuleExpressionException("\"everyone\" must be the boolean `true`.");
            }

            return new EveryoneCondition();
        }

        if (keys.SetEquals(new[] { "group" }))
        {
            return new GroupCondition(RequireNonEmptyString(element.GetProperty("group"), "group"));
        }

        if (keys.SetEquals(new[] { "user" }))
        {
            return new UserCondition(RequireNonEmptyString(element.GetProperty("user"), "user"));
        }

        if (keys.SetEquals(new[] { "attr", "in" }))
        {
            var attribute = RequireNonEmptyString(element.GetProperty("attr"), "attr");
            var inValue = element.GetProperty("in");
            if (inValue.ValueKind != JsonValueKind.Array || inValue.GetArrayLength() == 0)
            {
                throw new MalformedRuleExpressionException("\"in\" must be a non-empty array of strings.");
            }

            var values = new List<string>();
            foreach (var item in inValue.EnumerateArray())
            {
                values.Add(RequireNonEmptyString(item, "in[]"));
            }

            return new AttrCondition(attribute, values);
        }

        throw new MalformedRuleExpressionException(
            $"Unrecognized rule node shape with keys: {string.Join(", ", keys.OrderBy(k => k, StringComparer.Ordinal))}.");
    }

    private static IReadOnlyList<RuleNode> ParseChildren(JsonElement arrayElement, string combinatorName)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array || arrayElement.GetArrayLength() == 0)
        {
            throw new MalformedRuleExpressionException($"\"{combinatorName}\" must be a non-empty array.");
        }

        return arrayElement.EnumerateArray().Select(ParseNode).ToList();
    }

    private static string RequireNonEmptyString(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new MalformedRuleExpressionException($"\"{fieldName}\" must be a string.");
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MalformedRuleExpressionException($"\"{fieldName}\" must not be empty.");
        }

        return value;
    }

    private static JsonNode ToJsonNode(RuleNode node) => node switch
    {
        AllOfNode allOf => new JsonObject
        {
            ["allOf"] = new JsonArray(allOf.Children.Select(ToJsonNode).ToArray()),
        },
        AnyOfNode anyOf => new JsonObject
        {
            ["anyOf"] = new JsonArray(anyOf.Children.Select(ToJsonNode).ToArray()),
        },
        EveryoneCondition => new JsonObject { ["everyone"] = true },
        GroupCondition group => new JsonObject { ["group"] = group.Group },
        UserCondition user => new JsonObject { ["user"] = user.UserId },
        AttrCondition attr => new JsonObject
        {
            ["attr"] = attr.Attribute,
            ["in"] = new JsonArray(attr.In.Select(v => (JsonNode)v).ToArray()),
        },
        _ => throw new NotSupportedException($"Unknown rule node type {node.GetType()}."),
    };
}
