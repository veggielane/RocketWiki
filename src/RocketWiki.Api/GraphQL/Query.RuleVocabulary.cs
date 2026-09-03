using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// One registered attribute for the rule builder's attr-condition picker, shaped to
/// the SPA's contract (web/src/access/attributeOption.ts: key, displayName?,
/// allowedValues). Named after design.md §8's sketch
/// (<c>attributeRegistry: [AttributeDefinition!]!</c>); a view record rather than the
/// <c>AttributeDefinition</c> entity so the schema exposes exactly these three fields.
/// </summary>
[GraphQLName("AttributeDefinition")]
public sealed record AttributeDefinitionView(string Key, string? DisplayName, IReadOnlyList<string> AllowedValues);

public partial class Query
{
    private const string VocabularyAuditAction = "permission.vocabulary";

    /// <summary>
    /// Known group names for the rule builder's group picker (design.md §6.6/§8).
    ///
    /// Honesty about the source, because it matters: this API deliberately holds no
    /// Keycloak admin credential (§6.6 — "avoiding a Keycloak admin-API dependency in
    /// v1"), so this is NOT the realm's group list. It is the union of what this
    /// instance can locally know: the <c>KnownGroup</c> table (§6.6's accumulated/
    /// manually-added picker source), every group name referenced by an existing
    /// access-rule expression, and the groups on the caller's own validated token.
    /// It is suggestion-vocabulary, not authority — the rule engine accepts any
    /// string and matches it ordinally (§6.3), so the builder's freeSolo entry stays
    /// valid, and a name's absence here proves nothing about the realm.
    ///
    /// Gated to rule managers — instance admin, or space-admin of at least one space
    /// (§6.5.2's "who may manage access rules") — because the rule builder is the only
    /// consumer (§6.6) and the accumulated group list sketches the org structure;
    /// everyone else gets an empty list (absent, not forbidden, §6.7) while the gate
    /// refusal itself is audited (§7). No mirror data is read here: the local User row
    /// stores no groups, and nothing in this resolver feeds an authorization decision
    /// (§6.1).
    /// </summary>
    [AuditAction(VocabularyAuditAction)]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<string>> Groups(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        if (!await IsRuleManagerAsync(db, principal, instanceRoleAccessor, cancellationToken))
        {
            await AuditVocabularyDenialAsync(auditSink, cancellationToken);
            return [];
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);

        names.UnionWith(await db.KnownGroups.Select(g => g.Name).ToListAsync(cancellationToken));
        names.UnionWith(principal.Groups);

        var attributeValuesIgnored = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var expressionJson in await db.AccessRules.Select(r => r.ExpressionJson).ToListAsync(cancellationToken))
        {
            CollectFromExpression(expressionJson, names, attributeValuesIgnored);
        }

        return names.ToList();
    }

    /// <summary>
    /// The attribute registry for the rule builder (design.md §6.2/§8). Only
    /// registered attributes appear — the registry declares which claims are
    /// rule-usable, and an unregistered key can never match anyway because the
    /// token-built Principal only carries registered claims (§6.1/§6.2).
    ///
    /// <c>allowedValues</c> source, precisely: the definition's declared allowed
    /// values (§6.2 — "drives rule-builder dropdowns") when it has them; otherwise
    /// values observed locally — values already used in access-rule expressions for
    /// that key, the caller's own token values, and, for instance admins only,
    /// distinct values from the local User mirror's AttributesJson (§6.2 makes
    /// mirrored values instance-admin-visible only, so a space-admin's vocabulary
    /// never derives from other users' mirrored data). Like <see cref="Groups"/>,
    /// this is suggestion-vocabulary, not authority: the rule engine accepts any
    /// string ordinally, freeSolo entry stays valid, and reading the mirror here is
    /// display-side only — no authorization decision consumes it (§6.1).
    ///
    /// Same rule-manager gate and same absent-not-forbidden empty list as
    /// <see cref="Groups"/>, for the same §6.5.2/§6.6 reason.
    /// </summary>
    [AuditAction(VocabularyAuditAction)]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<AttributeDefinitionView>> AttributeRegistry(
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        if (!await IsRuleManagerAsync(db, principal, instanceRoleAccessor, cancellationToken))
        {
            await AuditVocabularyDenialAsync(auditSink, cancellationToken);
            return [];
        }

        var definitions = await db.AttributeDefinitions
            .OrderBy(d => d.Key)
            .ToListAsync(cancellationToken);
        if (definitions.Count == 0)
        {
            return [];
        }

        // Observed values, gathered once: per-key values used in existing rule
        // expressions, plus the caller's own token values.
        var groupNamesIgnored = new SortedSet<string>(StringComparer.Ordinal);
        var observedByKey = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var expressionJson in await db.AccessRules.Select(r => r.ExpressionJson).ToListAsync(cancellationToken))
        {
            CollectFromExpression(expressionJson, groupNamesIgnored, observedByKey);
        }

        foreach (var (key, values) in principal.Attributes)
        {
            ObservedValuesFor(observedByKey, key).UnionWith(values);
        }

        if (instanceRoleAccessor.IsInstanceAdmin)
        {
            foreach (var attributesJson in await db.Users.Select(u => u.AttributesJson).ToListAsync(cancellationToken))
            {
                CollectMirroredAttributes(attributesJson, observedByKey);
            }
        }

        return definitions
            .Select(d => new AttributeDefinitionView(
                d.Key,
                string.IsNullOrWhiteSpace(d.DisplayName) ? null : d.DisplayName,
                ParseDeclaredAllowedValues(d.AllowedValuesJson)
                    ?? (IReadOnlyList<string>?)observedByKey.GetValueOrDefault(d.Key)?.ToList()
                    ?? []))
            .ToList();
    }

    /// <summary>design.md §6.5.2's rule-manager set: instance admin, or space-admin of
    /// at least one space. The full grant set is small and already cached-in-memory
    /// territory per data-model.md, so computing this per request is cheap.</summary>
    private static async Task<bool> IsRuleManagerAsync(
        RocketWikiDbContext db, Principal principal, IInstanceRoleAccessor instanceRoleAccessor, CancellationToken cancellationToken)
    {
        if (instanceRoleAccessor.IsInstanceAdmin)
        {
            return true;
        }

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.RoleGrant)
            .ToListAsync(cancellationToken);

        return grants
            .GroupBy(g => g.SpaceId)
            .Any(spaceGrants => EffectivePermissionCalculator.ComputeSpaceRole(spaceGrants, principal) == SpaceRole.SpaceAdmin);
    }

    /// <summary>Same {"reason": ...} details shape as every other denial row (§7), with
    /// no subject: the vocabulary is instance-global, not a space or page. DbAuditSink
    /// then suppresses AuditFieldMiddleware's would-be Success row for the same
    /// (action, null-subject) pair, so a refused read never also claims success.</summary>
    private static Task AuditVocabularyDenialAsync(IAuditSink auditSink, CancellationToken cancellationToken) =>
        auditSink.RecordAsync(
            new AuditRecord(
                VocabularyAuditAction,
                AuditOutcome.Denied,
                DetailsJson: JsonSerializer.Serialize(new { reason = "not-rule-manager" })),
            cancellationToken);

    /// <summary>Walks one stored expression, collecting group names and per-attribute
    /// values. A malformed expression contributes nothing: the rule engine already
    /// fails closed on it (design.md §6.3), and a vocabulary has no business inventing
    /// tokens out of JSON the engine refuses to evaluate.</summary>
    private static void CollectFromExpression(
        string expressionJson, ISet<string> groupNames, IDictionary<string, SortedSet<string>> attributeValues)
    {
        if (!RuleExpressionSerializer.TryParse(expressionJson, out var node, out _) || node is null)
        {
            return;
        }

        CollectFromNode(node, groupNames, attributeValues);
    }

    private static void CollectFromNode(
        RuleNode node, ISet<string> groupNames, IDictionary<string, SortedSet<string>> attributeValues)
    {
        switch (node)
        {
            case AllOfNode allOf:
                foreach (var child in allOf.Children)
                {
                    CollectFromNode(child, groupNames, attributeValues);
                }

                break;
            case AnyOfNode anyOf:
                foreach (var child in anyOf.Children)
                {
                    CollectFromNode(child, groupNames, attributeValues);
                }

                break;
            case GroupCondition group:
                groupNames.Add(group.Group);
                break;
            case AttrCondition attr:
                ObservedValuesFor(attributeValues, attr.Attribute).UnionWith(attr.In);
                break;
        }
    }

    /// <summary>One user's mirrored AttributesJson ({"nationality": ["NZ"], ...} — see
    /// JitUserProvisioningMiddleware). Unparseable or oddly-shaped JSON contributes
    /// nothing rather than failing the query.</summary>
    private static void CollectMirroredAttributes(
        string? attributesJson, IDictionary<string, SortedSet<string>> attributeValues)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return;
        }

        Dictionary<string, string[]>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(attributesJson);
        }
        catch (JsonException)
        {
            return;
        }

        foreach (var (key, values) in parsed ?? new Dictionary<string, string[]>())
        {
            ObservedValuesFor(attributeValues, key).UnionWith(values.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
    }

    private static SortedSet<string> ObservedValuesFor(IDictionary<string, SortedSet<string>> attributeValues, string key)
    {
        if (!attributeValues.TryGetValue(key, out var values))
        {
            values = new SortedSet<string>(StringComparer.Ordinal);
            attributeValues[key] = values;
        }

        return values;
    }

    /// <summary>Declared allowed values win outright when present (§6.2 — the registry
    /// IS the dropdown); null (undeclared or unparseable) falls back to observed
    /// values. A declared-but-empty array is treated as undeclared rather than as
    /// "suggest nothing".</summary>
    private static IReadOnlyList<string>? ParseDeclaredAllowedValues(string? allowedValuesJson)
    {
        if (string.IsNullOrWhiteSpace(allowedValuesJson))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(allowedValuesJson);
            return values is { Length: > 0 } ? values : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
