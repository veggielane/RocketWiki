using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The realm's protocol mappers and the code that reads claims out of a token are one
/// contract across two files that no test connected. Every ABAC decision depends on it:
/// <c>groups</c> feeds <c>GroupCondition</c>, <c>nationality</c> and <c>clearance</c>
/// feed the §21 gate.
///
/// <para><b>This exact failure has already shipped once</b>, for <c>roles</c>, and
/// <c>IInstanceRoleAccessor</c>'s own doc records why the suite missed it: TestAuthHandler
/// builds its ClaimsPrincipal by hand and performs no inbound mapping, so a mismatch
/// between what Keycloak emits and what production reads is invisible to every
/// integration test. Only <c>roles</c> got a regression guard afterwards. The rest are
/// asserted in comments.</para>
///
/// <para>Two things can break it, and both are silent: a mapper renamed in the realm, and
/// IdentityModel adding one of these names to its default inbound claim-type map — after
/// which every group-based rule matches nobody while the whole suite stays green.</para>
/// </summary>
public class KeycloakClaimParityTests
{
    /// <summary>The claim names production reads straight off the validated token.</summary>
    private static readonly string[] RequiredClaimNames =
    [
        "groups",
        ClearanceGate.NationalityAttributeKey,
        ClearanceGate.ClearanceAttributeKey,
        "roles",
    ];

    private static JsonDocument LoadRealm()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "RocketWiki.AppHost", "keycloak", "rocketwiki-realm.json");
        Assert.True(File.Exists(path), $"Realm import not found at {path}; this test is the only thing reading it.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RocketWiki.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>Every "claim.name" any protocol mapper in the realm emits.</summary>
    private static HashSet<string> EmittedClaimNames(JsonDocument realm)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("claim.name") && property.Value.ValueKind == JsonValueKind.String)
                        {
                            names.Add(property.Value.GetString()!);
                        }

                        Walk(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
            }
        }

        Walk(realm.RootElement);
        return names;
    }

    [Fact]
    public void TheRealmEmitsEveryClaimTheCodeReads()
    {
        using var realm = LoadRealm();
        var emitted = EmittedClaimNames(realm);

        // Non-vacuous: a walk that found nothing would satisfy nothing below.
        Assert.NotEmpty(emitted);

        var missing = RequiredClaimNames.Where(name => !emitted.Contains(name)).ToList();
        Assert.True(missing.Count == 0,
            "The realm has no protocol mapper emitting these claims, which production reads directly off the token. " +
            "Every ABAC rule that depends on one would match nobody, silently:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void GroupsAreEmittedWithoutTheirFullPath()
    {
        // A group claim of "/engineering" never equals the "engineering" a GroupCondition
        // was written against, and nothing would report the mismatch — the rule simply
        // stops matching.
        using var realm = LoadRealm();
        var groupMapper = FindMapperEmitting(realm, "groups");

        Assert.True(groupMapper.TryGetProperty("config", out var config));
        Assert.True(config.TryGetProperty("full.path", out var fullPath),
            "The group mapper must state full.path explicitly; its default is true, which prefixes every group with '/'.");
        Assert.Equal("false", fullPath.GetString());
    }

    [Fact]
    public void NoneOfTheseClaimsAreRewrittenByIdentityModelsInboundMap()
    {
        // The structural risk, and the reason this is a test rather than a comment.
        // IdentityModel rewrites claim types it knows about; if a future version adds
        // "groups" (or nationality/clearance) to that map, FindAll("groups") returns
        // nothing, every group-based rule matches nobody, and the suite stays green
        // because TestAuthHandler builds its principal by hand and maps nothing.
        //
        // "roles" is deliberately absent from the list below: it IS in the default map,
        // which is exactly the bug that shipped, and IInstanceRoleAccessor reads it in
        // the mapped form on purpose.
        foreach (var claim in new[] { "groups", ClearanceGate.NationalityAttributeKey, ClearanceGate.ClearanceAttributeKey })
        {
            Assert.False(JsonWebTokenHandler.DefaultInboundClaimTypeMap.ContainsKey(claim),
                $"IdentityModel now rewrites the '{claim}' claim. Production reads it by that exact name off the " +
                "validated token, so every rule depending on it would silently match nobody. Either read the mapped " +
                "name or clear the mapping at startup.");

            Assert.False(JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.ContainsKey(claim),
                $"IdentityModel's legacy handler now rewrites '{claim}'; same consequence as above.");
        }
    }

    private static JsonElement FindMapperEmitting(JsonDocument realm, string claimName)
    {
        JsonElement? found = null;

        void Walk(JsonElement element)
        {
            if (found is not null)
            {
                return;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("config", out var config)
                    && config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty("claim.name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() == claimName)
                {
                    found = element;
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }

        Walk(realm.RootElement);
        Assert.True(found is not null, $"No protocol mapper in the realm emits a '{claimName}' claim.");
        return found!.Value;
    }
}
