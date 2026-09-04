using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §11 step 3: JIT provisioning of the local <c>User</c> row from token
/// claims, on every authenticated request. Exercised through real HTTP requests
/// against the running pipeline (JitUserProvisioningMiddleware), then verified by
/// reading the SQLite-backed DbContext directly in a fresh scope — the request's
/// own scope is gone by the time the test gets control back.
/// </summary>
public sealed class JitProvisioningTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task AuthenticatedRequest_CreatesLocalUserRow_WithMirroredAttributes()
    {
        var subject = $"jit-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: subject,
            email: "jit.test@example.test",
            name: "JIT Test User",
            groups: ["engineering"],
            nationality: ["NZ", "GB"]);

        await client.PostGraphQLAsync("{ me { id } }");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == subject);

        Assert.Equal("jit.test@example.test", user.Email);
        Assert.Equal("JIT Test User", user.DisplayName);
        Assert.False(user.IsExternal);

        var attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(user.AttributesJson)!;
        Assert.Equal(["NZ", "GB"], attributes["nationality"]);
    }

    /// <summary>
    /// design.md §6.2: the mirror records the <c>groups</c> claim, raw and in token order,
    /// for the profile page — and beside it only <c>nationality</c>. It used to record a
    /// clearance claim and every configured selector claim for the gates that read them;
    /// those gates are gone, and a claim off the list, however much it looks like one of
    /// them, is not stored. Mirror every claim on the token and this goes red on
    /// <c>vegetable</c>; mirror the old list and it goes red on <c>clearance</c>.
    /// </summary>
    [Fact]
    public async Task JitProvisioning_MirrorsGroupsAndNationality_AndNothingElse()
    {
        var subject = $"jit-groups-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: subject,
            email: "jit.groups@example.test",
            groups: ["propulsion", "engineering"],
            nationality: ["NZ"],
            roles: ["user"],
            claims:
            [
                // The claims the removed gates used to read, and one that merely looks
                // like one: none is an attribute now, none is mirrored.
                ("clearance", "SECRET"),
                ("fruit", " Yes "),
                ("vegetable", "yes"),
            ]);

        await client.PostGraphQLAsync("{ me { id } }");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == subject);

        var attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(user.AttributesJson)!;
        Assert.Equal(["groups", "nationality"], attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // Token order, verbatim: the profile page sorts for display; the mirror interprets nothing.
        Assert.Equal(["propulsion", "engineering"], attributes["groups"]);
        Assert.Equal(["NZ"], attributes["nationality"]);
    }

    [Fact]
    public async Task JitProvisioning_RecordsAnEmptyGroupsList_WhenTheTokenCarriesNone()
    {
        // Every key is present, empty when the token had no such claim, so a reader can
        // tell "recorded as none" from "never recorded by this build".
        var subject = $"jit-nogroups-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: subject);

        await client.PostGraphQLAsync("{ me { id } }");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == subject);

        var attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(user.AttributesJson)!;
        Assert.Empty(attributes["groups"]);
        Assert.Empty(attributes["nationality"]);
    }

    [Fact]
    public async Task SameSubjectTwice_UpsertsSameUserRow_DoesNotDuplicate()
    {
        var subject = $"jit-upsert-{Guid.NewGuid()}";
        var client = factory.CreateClient();

        client.SetTestUser(sub: subject, name: "First Name", nationality: ["NZ"]);
        await client.PostGraphQLAsync("{ me { id } }");

        client.SetTestUser(sub: subject, name: "Updated Name", nationality: ["US"]);
        await client.PostGraphQLAsync("{ me { id } }");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        Assert.Equal(1, await db.Users.CountAsync(u => u.Subject == subject));
        var user = await db.Users.SingleAsync(u => u.Subject == subject);
        Assert.Equal("Updated Name", user.DisplayName);
    }

    [Fact]
    public async Task AnonymousRequest_ProvisionsNoUserRow()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var countBefore = await db.Users.CountAsync();

        var client = factory.CreateClient();
        await client.PostGraphQLAsync("{ me { isAuthenticated } }");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(countBefore, await verifyDb.Users.CountAsync());
    }

    /// <summary>
    /// design.md §6.6: "known groups are accumulated from observed logins (plus manual
    /// add)". The accumulation never existed — nothing in the source tree wrote a
    /// KnownGroup row, so the table was permanently empty and the §6.6 picker could only
    /// offer names already used in a rule or held by the caller's own token. A group
    /// nobody had written a rule for yet was invisible to the admin trying to write the
    /// first one.
    ///
    /// <para>Asserted through the picker rather than the table, because the picker is
    /// what the feature is for: the observing user and the ADMIN reading the list are
    /// deliberately different people, so a passing test cannot be explained by
    /// Query.Groups' own "the caller's own token groups" arm.</para>
    /// </summary>
    [Fact]
    public async Task GroupsOnAnObservedLogin_AreAccumulated_AndOfferedToTheRuleBuilder()
    {
        var observedGroup = $"observed-{Guid.NewGuid():N}";

        var member = factory.CreateClient();
        member.SetTestUser(sub: $"jit-{Guid.NewGuid()}", groups: [observedGroup]);
        using (var _ = await member.PostGraphQLAsync("{ me { isAuthenticated } }"))
        {
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var row = await db.KnownGroups.SingleAsync(g => g.Name == observedGroup);
            Assert.Equal(Core.Enums.KnownGroupSource.ObservedAtLogin, row.Source);
        }

        // A DIFFERENT principal - an instance admin who is not in that group - now sees
        // it in the picker. Without the accumulation there is nothing for them to see.
        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"jit-admin-{Guid.NewGuid()}", roles: ["admin"]);
        using var groups = await admin.PostGraphQLAsync("{ groups }");

        Assert.Contains(
            observedGroup,
            groups.RootElement.GetProperty("data").GetProperty("groups").EnumerateArray()
                .Select(g => g.GetString()));
    }

    /// <summary>Repeated logins must not re-query or re-insert: the recorder's
    /// process-wide memo is what keeps this off the hot path (see KnownGroupRecorder),
    /// and a duplicate insert would hit the unique index rather than being idempotent.</summary>
    [Fact]
    public async Task TheSameGroupSeenRepeatedly_IsRecordedExactlyOnce()
    {
        var observedGroup = $"repeat-{Guid.NewGuid():N}";

        for (var i = 0; i < 3; i++)
        {
            var client = factory.CreateClient();
            client.SetTestUser(sub: $"jit-repeat-{i}-{Guid.NewGuid()}", groups: [observedGroup]);
            using var _ = await client.PostGraphQLAsync("{ me { isAuthenticated } }");
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(1, await db.KnownGroups.CountAsync(g => g.Name == observedGroup));
    }
}
