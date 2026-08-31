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
            Kind = AccessRuleKind.SpaceGrant, SpaceId = replica.Id, Role = SpaceRole.Viewer,
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
