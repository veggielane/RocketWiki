using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.6/§8: the rule builder's vocabularies. Both queries are honest local
/// enumeration (KnownGroups + rule expressions + the caller's own token; never a
/// Keycloak admin API), gated to rule managers (§6.5.2: instance admin or
/// space-admin of at least one space), absent-not-forbidden for everyone else with
/// the gate refusal audited (§7). AttributeRegistry additionally respects §6.2's
/// "mirrored values are visible to instance admins only": mirror-derived value
/// suggestions appear for instance admins, never for space-admins.
/// </summary>
public sealed class RuleVocabularyQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record VocabularySeed(string KnownGroupName, string RuleGroupName, string SpaceAdminGroup, string AttributeKey);

    private async Task<VocabularySeed> SeedVocabularySourcesAsync(
        string? attributeAllowedValuesJson = null, IReadOnlyList<string>? ruleAttrValues = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var seed = new VocabularySeed(
            KnownGroupName: $"known-{unique}",
            RuleGroupName: $"rule-{unique}",
            SpaceAdminGroup: $"vocab-admins-{unique}",
            AttributeKey: $"attr{unique}");

        db.KnownGroups.Add(new KnownGroup { Name = seed.KnownGroupName, Source = KnownGroupSource.Manual, FirstSeenAtUtc = DateTime.UtcNow });
        db.AttributeDefinitions.Add(new AttributeDefinition
        {
            Key = seed.AttributeKey,
            ClaimName = seed.AttributeKey,
            DisplayName = $"Attribute {unique}",
            Type = AttributeValueType.StringArray,
            AllowedValuesJson = attributeAllowedValuesJson,
        });

        var space = new Space { Key = $"RV{unique}"[..8], Name = "Vocabulary Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.SpaceAdmin,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition(seed.SpaceAdminGroup)),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new AnyOfNode(
            [
                new GroupCondition(seed.RuleGroupName),
                new AttrCondition(seed.AttributeKey, ruleAttrValues ?? ["from-rule-1", "from-rule-2"]),
            ])),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        await db.SaveChangesAsync();

        return seed;
    }

    private static string[] GroupsOf(JsonDocument result) =>
        result.RootElement.GetProperty("data").GetProperty("groups")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static JsonElement? RegistryEntry(JsonDocument result, string key)
    {
        foreach (var entry in result.RootElement.GetProperty("data").GetProperty("attributeRegistry").EnumerateArray())
        {
            if (entry.GetProperty("key").GetString() == key)
            {
                return entry;
            }
        }

        return null;
    }

    [Fact]
    public async Task Groups_ForSpaceAdmin_UnionsKnownGroupsRuleExpressionsAndOwnToken()
    {
        var seed = await SeedVocabularySourcesAsync();
        var ownGroup = $"own-token-{Guid.NewGuid():N}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"space-admin-{Guid.NewGuid()}", groups: [seed.SpaceAdminGroup, ownGroup]);

        var result = await client.PostGraphQLAsync("{ groups }");
        var groups = GroupsOf(result);

        Assert.Contains(seed.KnownGroupName, groups);
        Assert.Contains(seed.RuleGroupName, groups);
        Assert.Contains(seed.SpaceAdminGroup, groups);
        Assert.Contains(ownGroup, groups);
    }

    [Fact]
    public async Task Groups_ForNonManager_IsEmpty_AndTheRefusalIsAudited()
    {
        var seed = await SeedVocabularySourcesAsync();
        var sub = $"plain-user-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);

        var result = await client.PostGraphQLAsync("{ groups }");

        // Absent, not forbidden (design.md §6.7): an empty vocabulary, indistinguishable
        // from an instance that genuinely knows no groups...
        Assert.Empty(GroupsOf(result));
        Assert.DoesNotContain(seed.KnownGroupName, GroupsOf(result));

        // ...while §7 still gets its record of the refused read.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var userId = await db.Users.Where(u => u.Subject == sub).Select(u => u.Id).SingleAsync();
        var denied = await db.AuditEvents
            .Where(e => e.UserId == userId && e.Action == "permission.vocabulary" && e.Outcome == AuditOutcome.Denied)
            .ToListAsync();
        Assert.NotEmpty(denied);
        Assert.Contains("not-rule-manager", denied[0].DetailsJson);
    }

    [Fact]
    public async Task AttributeRegistry_DeclaredAllowedValues_AreAuthoritative()
    {
        var seed = await SeedVocabularySourcesAsync(
            attributeAllowedValuesJson: """["DECLARED-A","DECLARED-B"]""",
            ruleAttrValues: ["observed-should-not-appear"]);

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        var result = await admin.PostGraphQLAsync("{ attributeRegistry { key displayName allowedValues } }");
        var entry = RegistryEntry(result, seed.AttributeKey);

        Assert.NotNull(entry);
        var allowed = entry.Value.GetProperty("allowedValues").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "DECLARED-A", "DECLARED-B" }, allowed);
        Assert.False(string.IsNullOrEmpty(entry.Value.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task AttributeRegistry_Undeclared_ObservesRulesAndOwnToken_ButNeverTheMirror_ForSpaceAdmin()
    {
        var seed = await SeedVocabularySourcesAsync();

        // Another user's mirrored value for this key - §6.2: instance-admin-visible only.
        var mirrorValue = $"MIRROR-{Guid.NewGuid():N}"[..14];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            db.Users.Add(new User
            {
                Subject = $"mirrored-{Guid.NewGuid()}",
                DisplayName = "Mirrored User",
                AttributesJson = JsonSerializer.Serialize(new Dictionary<string, string[]> { [seed.AttributeKey] = [mirrorValue] }),
                CreatedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // The caller's own token value for the key IS fair game - it's their own data.
        // (TestAuthHandler only wires the "nationality" claim, so the own-token arm is
        // asserted via the instance-admin test below for nationality-shaped keys; here
        // the space-admin sees rule-observed values and never the mirror's.)
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"space-admin-{Guid.NewGuid()}", groups: [seed.SpaceAdminGroup]);

        var result = await client.PostGraphQLAsync("{ attributeRegistry { key allowedValues } }");
        var entry = RegistryEntry(result, seed.AttributeKey);

        Assert.NotNull(entry);
        var allowed = entry.Value.GetProperty("allowedValues").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("from-rule-1", allowed);
        Assert.Contains("from-rule-2", allowed);
        Assert.DoesNotContain(mirrorValue, allowed);
    }

    [Fact]
    public async Task AttributeRegistry_InstanceAdmin_AlsoSeesMirrorObservedValues()
    {
        var seed = await SeedVocabularySourcesAsync();
        var mirrorValue = $"MIRROR-{Guid.NewGuid():N}"[..14];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            db.Users.Add(new User
            {
                Subject = $"mirrored-{Guid.NewGuid()}",
                DisplayName = "Mirrored User",
                AttributesJson = JsonSerializer.Serialize(new Dictionary<string, string[]> { [seed.AttributeKey] = [mirrorValue] }),
                CreatedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        var result = await admin.PostGraphQLAsync("{ attributeRegistry { key allowedValues } }");
        var entry = RegistryEntry(result, seed.AttributeKey);

        Assert.NotNull(entry);
        var allowed = entry.Value.GetProperty("allowedValues").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(mirrorValue, allowed);
        Assert.Contains("from-rule-1", allowed);
    }

    [Fact]
    public async Task AttributeRegistry_ForNonManager_IsEmpty()
    {
        var seed = await SeedVocabularySourcesAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"plain-user-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync("{ attributeRegistry { key } }");

        var keys = result.RootElement.GetProperty("data").GetProperty("attributeRegistry")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();
        Assert.Empty(keys);
        Assert.DoesNotContain(seed.AttributeKey, keys);
    }

    [Fact]
    public async Task AttributeRegistry_OwnTokenValues_AppearForRegisteredKey()
    {
        // "nationality" is the one claim TestAuthHandler (like the real Keycloak
        // mapper) carries end to end - register it and check the caller's own token
        // values feed the suggestions. Registered with a unique display name so this
        // test tolerates the shared-fixture database.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            if (!await db.AttributeDefinitions.AnyAsync(d => d.Key == "nationality"))
            {
                db.AttributeDefinitions.Add(new AttributeDefinition
                {
                    Key = "nationality",
                    ClaimName = "nationality",
                    DisplayName = "Nationality",
                    Type = AttributeValueType.StringArray,
                    AllowedValuesJson = null,
                });
                await db.SaveChangesAsync();
            }
        }

        var ownValue = $"ZZ-{Guid.NewGuid():N}"[..8];
        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"], nationality: [ownValue]);

        var result = await admin.PostGraphQLAsync("{ attributeRegistry { key allowedValues } }");
        var entry = RegistryEntry(result, "nationality");

        Assert.NotNull(entry);
        var allowed = entry.Value.GetProperty("allowedValues").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(ownValue, allowed);
    }
}
