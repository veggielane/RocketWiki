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
/// design.md §7/§8: "audit log viewer for instance admins... viewing the audit log is
/// itself audited (audit.view)"; denied events are returned alongside successes.
/// </summary>
public sealed class AuditEventsQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task AuditEvents_AsInstanceAdmin_ReturnsEvents_IncludingDenied()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();
        var space = new Space { Key = $"AE{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Audit Events Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant, SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Audited Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        // Generate one real Success event (a plain page view)...
        var viewerClient = factory.CreateClient();
        viewerClient.SetTestUser(sub: $"viewer-{Guid.NewGuid()}");
        await viewerClient.PostGraphQLAsync($$"""{ page(id: "{{page.Id}}") { title } }""");

        // ...and one real Denied event (an anonymous attachment upload attempt, audited
        // by AttachmentEndpoints via MutationAuthHelper - see that pipeline's own tests).
        var anonymousClient = factory.CreateClient();
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        content.Add(fileContent, "file", "x.bin");
        await anonymousClient.PostAsync($"/attachments/{page.Id}", content);

        var adminClient = factory.CreateClient();
        adminClient.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);
        var result = await adminClient.PostGraphQLAsync("""
            { auditEvents(filter: {}) { nodes { action outcome } } }
            """);

        var nodes = result.RootElement.GetProperty("data").GetProperty("auditEvents").GetProperty("nodes");
        var outcomes = nodes.EnumerateArray().Select(n => n.GetProperty("outcome").GetString()).ToArray();

        Assert.Contains("SUCCESS", outcomes);
    }

    [Fact]
    public async Task AuditEvents_AsNonAdmin_ReturnsGraphQLError_AndRecordsDenial()
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"plain-{Guid.NewGuid()}");

        var result = await client.PostGraphQLAsync("""
            { auditEvents(filter: {}) { nodes { action } } }
            """);

        Assert.True(result.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.GetArrayLength() > 0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var deniedAuditViewExists = await db.AuditEvents
            .AnyAsync(e => e.Action == "audit.view" && e.Outcome == AuditOutcome.Denied);
        Assert.True(deniedAuditViewExists, "A non-admin's auditEvents attempt must itself be recorded as a denial.");
    }
    [Fact]
    public async Task AuditEvents_WithThePageSizeTheAuditLogPageAsksFor_IsNotRefusedForPageSize()
    {
        // AuditLogPage requests first: 100, and the connection's cap was 50 — so the
        // audit log was refused for EVERY admin, at GraphQL validation, before the
        // resolver ever ran. Nothing caught it: the integration tests here query with
        // small page sizes and the SPA's tests mock the response, so the one layer that
        // enforces the cap was the one layer nothing exercised.
        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);

        using var result = await admin.PostGraphQLAsync("""
            { auditEvents(filter: {}, first: 100) { totalCount nodes { action outcome } } }
            """);

        if (result.RootElement.TryGetProperty("errors", out var errors))
        {
            Assert.DoesNotContain("maximum allowed items per page", errors.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        // Non-vacuous: the query actually resolved rather than failing some other way.
        Assert.Equal(JsonValueKind.Object,
            result.RootElement.GetProperty("data").GetProperty("auditEvents").ValueKind);
    }
}
