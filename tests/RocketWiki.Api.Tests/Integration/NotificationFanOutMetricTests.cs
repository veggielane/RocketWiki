using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The fan-out disposition vocabulary (design.md §15: bounded tags), asserted end to
/// end through one real event with all three outcomes at once: a connected recipient
/// who passes canView (<c>delivered_live</c>), a recipient with no connection
/// (<c>deferred_offline</c> — a blind row was persisted for fetch-time gating, NOT a
/// drop), and a connected recipient whose canView fails at send time
/// (<c>skipped_not_viewable</c> — the only disposition that persists nothing). In the
/// non-parallel telemetry collection so the counts are exact, per that collection's
/// own doc.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class NotificationFanOutMetricTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private static readonly string[] KnownDispositions =
    [
        ApiTelemetry.NotificationDeliveredLive,
        ApiTelemetry.NotificationDeferredOffline,
        ApiTelemetry.NotificationSkippedNotViewable,
    ];

    private HttpClient AuthedClient(string sub, IEnumerable<string>? nationality = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, name: sub, nationality: nationality);
        return client;
    }

    private async Task<Guid> ProvisionUserAsync(string sub, IEnumerable<string>? nationality = null)
    {
        var client = AuthedClient(sub, nationality);
        using var _ = await client.PostGraphQLAsync("query { me { isAuthenticated } }");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Subject == sub)).Id;
    }

    private async Task<HubConnection> ConnectAsync(string sub, IEnumerable<string>? nationality = null)
    {
        var claimsHeader = TestUserHttpClientExtensions.BuildEncodedClaimsHeaderValue(sub, name: sub, nationality: nationality);
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthHandler.ClaimsHeaderName, claimsHeader);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task OneFanOut_RecordsEachDispositionOnce_WithTheBoundedVocabulary()
    {
        // A US-only page, so a connected NZ recipient fails send-time canView.
        Guid pageId;
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
            db.Users.Add(seeder);
            await db.SaveChangesAsync();
            var space = new Space { Key = $"FM{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Fanout Metrics", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
            db.Spaces.Add(space);
            db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
            {
                Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
                ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            }));
            var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "metrics", Title = "Metrics Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
            db.Pages.Add(page);
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction, PageId = page.Id, Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            });
            await db.SaveChangesAsync();
            pageId = page.Id;
        }

        var liveSub = $"live-{Guid.NewGuid()}";
        var liveId = await ProvisionUserAsync(liveSub, nationality: ["US"]);
        var offlineSub = $"deferred-{Guid.NewGuid()}";
        var offlineId = await ProvisionUserAsync(offlineSub, nationality: ["US"]);
        var blockedSub = $"blocked-{Guid.NewGuid()}";
        var blockedId = await ProvisionUserAsync(blockedSub, nationality: ["NZ"]);

        await using var liveConnection = await ConnectAsync(liveSub, nationality: ["US"]);
        await using var blockedConnection = await ConnectAsync(blockedSub, nationality: ["NZ"]);
        // offlineSub deliberately never connects.

        var actorSub = $"metric-actor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub, nationality: ["US"]);

        using var collector = new MetricCollector<long>(ApiTelemetry.Meter, "rocketwiki.notifications.fanout");

        var content = $"cc @[{liveSub}](user://{liveId}), @[{offlineSub}](user://{offlineId}), @[{blockedSub}](user://{blockedId})";
        using var result = await AuthedClient(actorSub, nationality: ["US"]).PostGraphQLAsync($$"""
            mutation {
              updatePageContent(input: {
                pageId: "{{pageId}}", expectedRevisionNumber: 0,
                title: "Metrics Page", content: {{JsonSerializer.Serialize(content)}}, editSummary: null
              }) { page { id } error { kind message } }
            }
            """);
        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("updatePageContent").GetProperty("error").ValueKind);

        var measurements = collector.GetMeasurementSnapshot();

        // Exactly one measurement per disposition, each counting one Mention recipient.
        foreach (var disposition in KnownDispositions)
        {
            var match = Assert.Single(measurements, m =>
                Equals(m.Tags[ApiTelemetry.NotificationDispositionTag], disposition));
            Assert.Equal(1, match.Value);
            Assert.Equal(nameof(NotificationType.Mention), match.Tags[ApiTelemetry.NotificationTypeTag]);
        }

        // The vocabulary is closed (§15): nothing outside the three constants above -
        // in particular not the retired "delivered"/"skipped_offline" pair, whose
        // semantics this vocabulary replaced when the offline gap closed.
        Assert.All(measurements, m =>
            Assert.Contains(m.Tags[ApiTelemetry.NotificationDispositionTag], KnownDispositions.Cast<object?>()));

        // And the dispositions were honest about persistence: live row with snapshot,
        // deferred row without, no row at all for the not-viewable recipient.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var liveRow = Assert.Single(await db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == liveId).ToListAsync());
            Assert.NotNull(liveRow.TitleSnapshot);
            var deferredRow = Assert.Single(await db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == offlineId).ToListAsync());
            Assert.Null(deferredRow.TitleSnapshot);
            Assert.Empty(await db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == blockedId).ToListAsync());
        }
    }
}
