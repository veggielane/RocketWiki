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
/// design.md §6.5: a space's owner is the designated accountable person — <b>governance
/// metadata that confers no access</b>.
///
/// <para>That separation is the whole design, so it is what these tests are mostly about.
/// On this system access is the ABAC grants and nothing else, computed by the rule engine
/// with no side-channel; if ownership ever started granting rights it would be precisely
/// the "admin bypass" §6.5 forbids — a second way to acquire management rights outside the
/// grant model, and one that no permission review would think to look at.</para>
/// </summary>
public sealed class SpaceOwnerTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    /// <summary>Seeds a space with no grants at all, owned by <paramref name="ownerUserId"/>.</summary>
    private async Task<(User Seeder, Space Space)> SeedUngrantedSpaceAsync(Guid? ownerUserId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var seeder = new User
        {
            Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"OW{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Owner Test Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
            OwnerUserId = ownerUserId ?? seeder.Id,
        };
        db.Spaces.Add(space);
        await db.SaveChangesAsync();

        return (seeder, space);
    }

    private async Task<Guid> LocalUserIdOf(string sub)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return (await db.Users.SingleAsync(u => u.Subject == sub)).Id;
    }

    /// <summary>
    /// <b>The invariant the feature rests on</b>, exercised through the real pipeline
    /// rather than against the calculator: being a space's owner, with no grant, gets you
    /// nothing. This runs the actual `spaces` query as the owner, so it fails if anything
    /// anywhere — the rule engine, a resolver shortcut, a future convenience — ever starts
    /// treating ownership as a role.
    /// </summary>
    [Fact]
    public async Task Owner_WithNoGrant_CannotSeeTheSpaceAtAll()
    {
        var sub = $"owner-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);

        // Provision the local user, then make them the owner of a space they hold no
        // grant in — the exact shape a bypass would light up on.
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var ownerUserId = await LocalUserIdOf(sub);
        var (_, space) = await SeedUngrantedSpaceAsync(ownerUserId);

        using var result = await client.PostGraphQLAsync("{ spaces { key } }");
        var keys = result.RootElement.GetProperty("data").GetProperty("spaces")
            .EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.DoesNotContain(space.Key, keys);

        // §6.7: absent, not a stub — owning a space you cannot read must not even
        // confirm that it exists.
        using var byKey = await client.PostGraphQLAsync($$"""{ space(key: "{{space.Key}}") { key } }""");
        Assert.Equal(
            JsonValueKind.Null,
            byKey.RootElement.GetProperty("data").GetProperty("space").ValueKind);
    }

    /// <summary>
    /// The companion to the above: ownership grants nothing, so it must not grant the
    /// right to reassign itself either. A non-admin owner is refused, and the refusal is
    /// audited like every other denial (§7).
    /// </summary>
    [Fact]
    public async Task SetSpaceOwner_ByANonAdmin_IsRefusedAndAudited()
    {
        var sub = $"nonadmin-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var callerId = await LocalUserIdOf(sub);

        // The caller IS the current owner — and still cannot reassign, because the gate is
        // canManageAccess, not ownership.
        var (_, space) = await SeedUngrantedSpaceAsync(callerId);

        using var result = await client.PostGraphQLAsync($$"""
            mutation { setSpaceOwner(input: { spaceId: "{{space.Id}}", ownerUserId: "{{callerId}}" })
                { space { key } error { __typename } } }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("setSpaceOwner");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("space").ValueKind);
        Assert.Equal(JsonValueKind.Object, payload.GetProperty("error").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.True(
            await db.AuditEvents.AnyAsync(e =>
                e.Action == "space.owner.set" && e.UserId == callerId && e.Outcome == AuditOutcome.Denied),
            "a refused ownership change must still be audited (§7)");
    }

    /// <summary>
    /// §6.6: <c>canManageAccess</c> is what an owner-reassign control gates on, so it
    /// must agree with <c>setSpaceOwner</c>'s own gate — instance admin OR this space's
    /// space-admin, via the shared <c>RuleManagementGate</c>.
    ///
    /// <para>The space-admin case is the one that matters. Collapsing this to
    /// instance-admin-only would still pass a naive "a viewer sees false" test while
    /// hiding the control from every legitimate space admin — a field that is wrong in the
    /// restrictive direction fails silently, because nobody reports a button they never
    /// knew existed.</para>
    /// </summary>
    [Theory]
    [InlineData(SpaceRole.SpaceAdmin, true)]
    [InlineData(SpaceRole.Editor, false)]
    [InlineData(null, false)] // access only: may see, holds no role
    public async Task CanManageAccess_IsTrueOnlyForSpaceAdmins(SpaceRole? role, bool expected)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var seeder = new User
        {
            Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"MA{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Manage Access Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
            OwnerUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = role is null ? AccessRuleKind.AccessGrant : AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = role,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id,
            UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        }));
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"member-{Guid.NewGuid()}");

        using var result = await client.PostGraphQLAsync(
            $$"""{ space(key: "{{space.Key}}") { canManageAccess } }""");

        Assert.Equal(
            expected,
            result.RootElement.GetProperty("data").GetProperty("space")
                .GetProperty("canManageAccess").GetBoolean());
    }

    /// <summary>
    /// The field is rendered per row in a space list, which is the N+1 shape — so it
    /// resolves through <c>SpaceAccessFactsBySpaceIdDataLoader</c> rather than querying per
    /// space. This exercises it across the <c>spaces</c> list, where a per-space resolver
    /// would still be correct but would scale with the caller's space count, and where a
    /// batched loader that mixed up its keys would return the wrong row's answer.
    /// </summary>
    [Fact]
    public async Task CanManageAccess_IsAnsweredPerSpace_AcrossAList()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var seeder = new User
        {
            Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        // Two spaces the same caller sees at once, with DIFFERENT roles — so a loader
        // that returned one answer for the whole batch, or keyed them wrongly, is caught.
        var adminSpace = NewSpace(seeder.Id, "AD");
        var viewerSpace = NewSpace(seeder.Id, "VW");
        db.Spaces.AddRange(adminSpace, viewerSpace);
        db.AccessRules.AddRange(
            Grant(adminSpace.Id, SpaceRole.SpaceAdmin, seeder.Id),
            Grant(viewerSpace.Id, null, seeder.Id));
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"member-{Guid.NewGuid()}");

        using var result = await client.PostGraphQLAsync("{ spaces { key canManageAccess } }");

        var byKey = result.RootElement.GetProperty("data").GetProperty("spaces")
            .EnumerateArray()
            .ToDictionary(
                s => s.GetProperty("key").GetString()!,
                s => s.GetProperty("canManageAccess").GetBoolean());

        Assert.True(byKey[adminSpace.Key]);
        Assert.False(byKey[viewerSpace.Key]);
    }

    private static Space NewSpace(Guid seederId, string prefix) => new()
    {
        Key = $"{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
        Name = "List Space",
        OriginInstanceId = "standalone",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = seederId,
        OwnerUserId = seederId,
    };

    private static AccessRule Grant(Guid spaceId, SpaceRole? role, Guid seederId) => new()
    {
        Kind = role is null ? AccessRuleKind.AccessGrant : AccessRuleKind.RoleGrant, SpaceId = spaceId, Role = role,
        ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
        CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seederId,
        UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seederId,
    };

    /// <summary>
    /// Ownership grants nothing here either. If this said true for an owner, the client
    /// would render a reassign control that <c>setSpaceOwner</c> then refuses — the read
    /// gate and the write gate must give the same answer.
    /// </summary>
    [Fact]
    public async Task CanManageAccess_IsFalseForTheOwnerWithoutASpaceAdminGrant()
    {
        var sub = $"owner-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);
        using (await client.PostGraphQLAsync("{ me { id } }")) { }
        var ownerUserId = await LocalUserIdOf(sub);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var space = new Space
        {
            Key = $"OO{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Owned But Not Administered",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = ownerUserId,
            OwnerUserId = ownerUserId,
        };
        db.Spaces.Add(space);

        // Viewer, so the space resolves at all — but not space-admin.
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant, SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = ownerUserId,
            UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = ownerUserId,
        });
        await db.SaveChangesAsync();

        using var result = await client.PostGraphQLAsync(
            $$"""{ space(key: "{{space.Key}}") { canManageAccess } }""");

        Assert.False(
            result.RootElement.GetProperty("data").GetProperty("space")
                .GetProperty("canManageAccess").GetBoolean());
    }

    /// <summary>
    /// A replica materialised by sync carries <c>OwnerUserId = Guid.Empty</c> — users do
    /// not cross the boundary (§12), so there is no local user to inherit. The schema must
    /// say so honestly rather than inventing a name, which is why <c>Space.owner</c> is
    /// nullable while the author-shaped UserRef fields are not.
    /// </summary>
    [Fact]
    public async Task Owner_IsNull_WhenTheIdResolvesToNobody()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var seeder = new User
        {
            Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var replica = new Space
        {
            Key = $"RP{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Imported Space",
            OriginInstanceId = "some-low-instance",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.Empty,
            OwnerUserId = Guid.Empty,
        };
        db.Spaces.Add(replica);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant, SpaceId = replica.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id,
            UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");

        using var result = await client.PostGraphQLAsync(
            $$"""{ space(key: "{{replica.Key}}") { key owner { displayName } } }""");

        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        var space = result.RootElement.GetProperty("data").GetProperty("space");
        Assert.Equal(JsonValueKind.Null, space.GetProperty("owner").ValueKind);
    }
}
