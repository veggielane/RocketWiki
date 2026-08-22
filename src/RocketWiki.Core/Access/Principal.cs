namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.1: the principal built fresh, per request, from the validated access
/// token — never from the local User mirror. Group and attribute-value comparisons are
/// ordinal (case-sensitive): Keycloak group paths and registered attribute values (e.g.
/// ISO country codes) are treated as exact tokens, not free text.
/// </summary>
/// <param name="UserId">The `sub` claim.</param>
/// <param name="Groups">The `groups` claim.</param>
/// <param name="Attributes">
/// Registered attribute values from token claims (design.md §6.2). Every value is
/// represented as a list so single-valued (string) and multi-valued (string[])
/// attributes — e.g. dual nationality — are matched the same way: a condition passes
/// if any held value matches. A key absent from this dictionary means the principal
/// has no value for that attribute, which fails closed against any condition that
/// tests it.
/// </param>
public sealed record Principal(
    string UserId,
    IReadOnlySet<string> Groups,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes)
{
    public static Principal Create(
        string userId,
        IEnumerable<string> groups,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? attributes = null)
    {
        var groupSet = new HashSet<string>(groups, StringComparer.Ordinal);
        var attributeMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (attributes is not null)
        {
            foreach (var (key, values) in attributes)
            {
                attributeMap[key] = values;
            }
        }

        return new Principal(userId, groupSet, attributeMap);
    }
}
