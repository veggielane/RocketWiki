using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §7/§8: the site-admin user list. Gated and audited like the audit viewer —
/// the existing instance-admin-gated read — and returning only what the database
/// actually holds about a person.
///
/// <para>The thing worth testing hardest is the gate, because the failure is quiet: a
/// roster is an ordinary-looking list, so a gate that stopped working would produce a
/// page that renders perfectly for the wrong person. And the thing worth testing second
/// is what is NOT in the payload — <c>User.AttributesJson</c> mirrors nationality (§6.2,
/// admin-visible only), and the field that leaks it would be added by someone helpfully
/// "returning the whole user".</para>
/// </summary>
public sealed class UsersQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string RosterQuery = """
        { users { totalCount nodes { id subject displayName email isExternal hasAvatar createdAtUtc lastSeenAtUtc } } }
        """;

    private async Task<User> SeedUserAsync(string displayName, string? nationality = null, bool isExternal = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var user = new User
        {
            Subject = isExternal ? null : $"seed-{Guid.NewGuid()}",
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@example.test",
            IsExternal = isExternal,
            AttributesJson = nationality is null
                ? "{}"
                : $$"""{"nationality":["{{nationality}}"]}""",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Users_AsInstanceAdmin_ReturnsTheRoster()
    {
        var seeded = await SeedUserAsync($"Zz Roster {Guid.NewGuid():N}"[..24]);

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        using var result = await admin.PostGraphQLAsync(RosterQuery);

        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        var users = result.RootElement.GetProperty("data").GetProperty("users");
        var nodes = users.GetProperty("nodes").EnumerateArray().ToList();

        var row = Assert.Single(nodes, n => n.GetProperty("id").GetGuid() == seeded.Id);
        Assert.Equal(seeded.DisplayName, row.GetProperty("displayName").GetString());
        Assert.Equal(seeded.Email, row.GetProperty("email").GetString());
        Assert.Equal(seeded.Subject, row.GetProperty("subject").GetString());
        Assert.False(row.GetProperty("isExternal").GetBoolean());
        Assert.False(row.GetProperty("hasAvatar").GetBoolean());
        Assert.True(users.GetProperty("totalCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Users_AsNonAdmin_IsRefused_AndTheDenialIsAudited()
    {
        // The gate. A non-admin gets an explicit error rather than an empty list —
        // auditEvents' deliberate departure from "absent, not forbidden", for the same
        // reason: there is no existence to leak, and an honest refusal is more useful on
        // an unambiguously admin-only screen.
        var sub = $"plain-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub);

        using var result = await client.PostGraphQLAsync(RosterQuery);

        Assert.True(result.RootElement.TryGetProperty("errors", out var errors));
        Assert.Contains("Instance admin required", errors.GetRawText(), StringComparison.Ordinal);

        // No roster came back with the error.
        if (result.RootElement.TryGetProperty("data", out var data) && data.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("users").ValueKind);
        }

        // §7: the refusal is recorded, with the same {"reason": …} shape every other
        // denial row uses.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == sub);
        var denial = await db.AuditEvents.SingleAsync(e =>
            e.UserId == user.Id && e.Action == "admin.users.view" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains("instance admin required", denial.DetailsJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Users_Anonymously_IsRefused_AndWritesNoAuditRow()
    {
        // Same stance as auditEvents: there is no acting user to attribute a row to, and
        // DbAuditSink refuses UserId-less rows by design, so recording unconditionally
        // would make an anonymous probe fail with THAT refusal instead of this field's
        // own honest one.
        using var before = factory.Services.CreateScope();
        var beforeDb = before.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var rowsBefore = await beforeDb.AuditEvents.CountAsync(e => e.Action == "admin.users.view");

        var anonymous = factory.CreateClient();
        using var result = await anonymous.PostGraphQLAsync(RosterQuery);

        Assert.True(result.RootElement.TryGetProperty("errors", out _));

        using var after = factory.Services.CreateScope();
        var afterDb = after.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(rowsBefore, await afterDb.AuditEvents.CountAsync(e => e.Action == "admin.users.view"));
    }

    [Fact]
    public async Task Users_ReadingTheRoster_IsItselfAudited()
    {
        // A list of every account, with email addresses and last-seen times, is exactly
        // the kind of read worth knowing someone performed — the same reasoning that
        // makes viewing the audit log audited.
        var sub = $"admin-{Guid.NewGuid()}";
        var admin = factory.CreateClient();
        admin.SetTestUser(sub: sub, roles: ["admin"]);

        using var result = await admin.PostGraphQLAsync(RosterQuery);
        Assert.False(result.RootElement.TryGetProperty("errors", out _));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == sub);
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.UserId == user.Id && e.Action == "admin.users.view" && e.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Users_NeverExposesMirroredAttributes()
    {
        // User.AttributesJson mirrors registered attributes — on this instance that means
        // nationality, sensitive personal data §6.2 keeps admin-visible only. The roster
        // type is a projection precisely so this cannot ride along, and the field that
        // would leak it is the one someone adds while "returning the whole user".
        const string Sentinel = "ZZNATIONALITYZZ";
        var seeded = await SeedUserAsync($"Attr {Guid.NewGuid():N}"[..20], nationality: Sentinel);

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        using var result = await admin.PostGraphQLAsync(RosterQuery);

        // Non-vacuous: that user really is in the payload, so the sentinel's absence is
        // about the field set rather than about the row being missing.
        var nodes = result.RootElement.GetProperty("data").GetProperty("users").GetProperty("nodes");
        Assert.Contains(nodes.EnumerateArray(), n => n.GetProperty("id").GetGuid() == seeded.Id);
        Assert.DoesNotContain(Sentinel, result.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The field simply does not exist on the type, so no future selection can
        // reach it — asserted against the type’s own field set rather than by sending
        // an invalid query, which GraphQL rejects at validation (HTTP 400) and would
        // pass whether or not the field was there.
        var rosterFields = typeof(RocketWiki.Api.GraphQL.AdminUser)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();
        Assert.NotEmpty(rosterFields);
        Assert.DoesNotContain("AttributesJson", rosterFields);
    }

    [Fact]
    public async Task Users_OrdersByDisplayName()
    {
        // Stable and alphabetical: a roster is read by looking someone up, and id breaks
        // ties so paging does not reshuffle between requests.
        var marker = $"Order{Guid.NewGuid():N}"[..12];
        await SeedUserAsync($"{marker} Zoe");
        await SeedUserAsync($"{marker} Adam");
        await SeedUserAsync($"{marker} Mia");

        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        using var result = await admin.PostGraphQLAsync("{ users(first: 50) { nodes { displayName } } }");

        var names = result.RootElement.GetProperty("data").GetProperty("users").GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("displayName").GetString()!)
            .Where(n => n.StartsWith(marker, StringComparison.Ordinal))
            .ToList();

        Assert.Equal([$"{marker} Adam", $"{marker} Mia", $"{marker} Zoe"], names);
    }

}
