using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §12's server-resolved replica flag (the "mirrored from LOW — read-only"
/// banner data the SPA could not previously obtain) and §6.5.1's archived-space
/// listing that feeds restoreSpace, scoped to exactly who the restore mutation would
/// accept (instance admin or that space's own space-admin — the conservative reading
/// of §17's archived-visibility open question).
/// </summary>
public sealed class ReplicaFlagAndArchivedSpacesTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<User> SeedUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        return seeder;
    }

    /// <summary>Seeds a space with its grants. A role grant normally gets its mirror access
    /// grant beside it (design.md §6.4: an editor of a space can also see it);
    /// <paramref name="roleOnly"/> seeds the bare role grant, for the tests about a role
    /// holder who cannot see the space's content (§6.5.2).</summary>
    private async Task<Space> SeedSpaceAsync(
        User seeder, string originInstanceId, bool archived = false, bool roleOnly = false, params (SpaceRole? Role, RuleNode Expression)[] grants)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var space = new Space
        {
            Key = $"RA{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Replica/Archive Space",
            OriginInstanceId = originInstanceId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
            IsDeleted = archived,
            DeletedAtUtc = archived ? DateTime.UtcNow : null,
            DeletedByUserId = archived ? seeder.Id : null,
        };
        db.Spaces.Add(space);
        foreach (var (role, expression) in grants)
        {
            var rule = new AccessRule
            {
                Kind = role is null ? AccessRuleKind.AccessGrant : AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = role,
                ExpressionJson = RuleExpressionSerializer.Serialize(expression),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            };
            if (roleOnly)
            {
                db.AccessRules.Add(rule);
            }
            else
            {
                db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(rule));
            }
        }

        await db.SaveChangesAsync();
        return space;
    }

    [Fact]
    public async Task Space_IsReplica_FalseForNative_TrueForReplica_WithOriginVisible()
    {
        var seeder = await SeedUserAsync();
        // "standalone" is the factory's configured local instance id.
        var native = await SeedSpaceAsync(seeder, "standalone", grants: [(null, new EveryoneCondition())]);
        var replica = await SeedSpaceAsync(seeder, "LOW", grants: [(null, new EveryoneCondition())]);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync("{ spaces { key isReplica originInstanceId } }");
        var byKey = result.RootElement.GetProperty("data").GetProperty("spaces")
            .EnumerateArray()
            .ToDictionary(e => e.GetProperty("key").GetString()!);

        Assert.False(byKey[native.Key].GetProperty("isReplica").GetBoolean());
        Assert.True(byKey[replica.Key].GetProperty("isReplica").GetBoolean());
        // The banner's origin name: already carried to every viewer by
        // ReadOnlyReplicaError (design.md §12), so reading it proactively leaks nothing new.
        Assert.Equal("LOW", byKey[replica.Key].GetProperty("originInstanceId").GetString());
    }

    [Fact]
    public async Task ArchivedSpaces_InstanceAdmin_SeesListingWithRestoreShape()
    {
        var seeder = await SeedUserAsync();
        var archived = await SeedSpaceAsync(seeder, "standalone", archived: true,
            grants: [(null, new EveryoneCondition())]);

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        var result = await admin.PostGraphQLAsync("{ archivedSpaces { id key name archivedAtUtc } }");
        var entry = result.RootElement.GetProperty("data").GetProperty("archivedSpaces")
            .EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == archived.Key);

        Assert.Equal(archived.Id.ToString(), entry.GetProperty("id").GetString());
        Assert.Equal(archived.Name, entry.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("archivedAtUtc").ValueKind);
    }

    [Fact]
    public async Task ArchivedSpaces_SpaceAdminOfThatSpace_SeesOnlyTheirs()
    {
        // A role grant ALONE lists the space for its holder (design.md §6.4/§6.5.2's space
        // visibility, the same rule the live listing uses): the Space-admin who holds no
        // access grant must still find the space they administer. A space whose grants
        // the caller matches in neither kind is absent.
        var seeder = await SeedUserAsync();
        var adminGroup = $"arch-admins-{Guid.NewGuid():N}";
        var theirSpace = await SeedSpaceAsync(seeder, "standalone", archived: true, roleOnly: true,
            grants: [(SpaceRole.SpaceAdmin, new GroupCondition(adminGroup))]);
        var someoneElses = await SeedSpaceAsync(seeder, "standalone", archived: true, roleOnly: true,
            grants: [(SpaceRole.SpaceAdmin, new GroupCondition($"other-admins-{Guid.NewGuid():N}"))]);

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"space-admin-{Guid.NewGuid()}", groups: [adminGroup]);

        var result = await client.PostGraphQLAsync("{ archivedSpaces { key } }");
        var keys = result.RootElement.GetProperty("data").GetProperty("archivedSpaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.Contains(theirSpace.Key, keys);
        Assert.DoesNotContain(someoneElses.Key, keys);
    }

    [Fact]
    public async Task ArchivedSpaces_AccessGrantHolder_SeesTheSpace_LikeAnyListing()
    {
        // Space visibility (design.md §6.4/§6.5.2): an access grant lists the space for
        // its holder, archived or live - the same rule SpaceReads applies. Restoring it
        // is a separate manage gate the mutation enforces on its own.
        var seeder = await SeedUserAsync();
        var archived = await SeedSpaceAsync(seeder, "standalone", archived: true,
            grants: [(null, new EveryoneCondition())]);

        var reader = factory.CreateClient();
        reader.SetTestUser(sub: $"reader-{Guid.NewGuid()}");

        var result = await reader.PostGraphQLAsync("{ archivedSpaces { key } }");
        var keys = result.RootElement.GetProperty("data").GetProperty("archivedSpaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.Contains(archived.Key, keys);
    }

    [Fact]
    public async Task ArchivedSpaces_NoGrantAdmitsTheCaller_GetsEmptyList_AbsentNotForbidden()
    {
        // The §6.7-shaped negative: a caller matched by no grant of either kind gets an
        // empty list, indistinguishable from "nothing is archived" - no error, no denial
        // row, no hint that the space exists.
        var seeder = await SeedUserAsync();
        var archived = await SeedSpaceAsync(seeder, "standalone", archived: true,
            grants: [(null, new GroupCondition($"insiders-{Guid.NewGuid():N}"))]);

        var stranger = factory.CreateClient();
        stranger.SetTestUser(sub: $"stranger-{Guid.NewGuid()}");

        var result = await stranger.PostGraphQLAsync("{ archivedSpaces { key } }");
        Assert.Equal(JsonValueKind.Undefined, result.RootElement.TryGetProperty("errors", out var errors) ? errors.ValueKind : JsonValueKind.Undefined);
        var keys = result.RootElement.GetProperty("data").GetProperty("archivedSpaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.DoesNotContain(archived.Key, keys);
    }
}
