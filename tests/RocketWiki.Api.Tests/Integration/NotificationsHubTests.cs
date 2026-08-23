using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8: /hubs/notifications carries both durable per-user notifications and
/// ephemeral page-scoped presence. TestServer has no real Kestrel socket for
/// WebSockets, so every connection here forces LongPolling — the documented way to
/// exercise a SignalR hub against WebApplicationFactory.
/// </summary>
public sealed class NotificationsHubTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
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

    private async Task<(Space Space, Page Page)> SeedViewablePageAsync(string? viewRestrictionNationality = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"HB{Guid.NewGuid():N}"[..8], Name = "Hub Test Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Hub Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        if (viewRestrictionNationality is not null)
        {
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction, PageId = page.Id, Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", [viewRestrictionNationality])),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            });
            await db.SaveChangesAsync();
        }

        return (space, page);
    }

    [Fact]
    public async Task JoinPage_OnViewablePage_BroadcastsViewersChanged_AndRegistersPresence()
    {
        var (_, page) = await SeedViewablePageAsync();
        await using var connection = await ConnectAsync($"joiner-{Guid.NewGuid()}");

        var viewersChanged = new TaskCompletionSource<object[]>();
        connection.On<object[]>("ViewersChanged", views => viewersChanged.TrySetResult(views));

        await connection.InvokeAsync("JoinPage", page.Id);
        var views = await viewersChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(views);

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers(page.Id));
    }

    [Fact]
    public async Task JoinPage_OnRestrictedPage_SilentlyNoOps_NoPresenceRegistered()
    {
        // Restricted to US nationals; this connection is NZ, so canView fails.
        var (_, page) = await SeedViewablePageAsync(viewRestrictionNationality: "US");
        await using var connection = await ConnectAsync($"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        await connection.InvokeAsync("JoinPage", page.Id);

        // Absent, not forbidden (design.md §6.7): no exception, no event, just nothing
        // registered - the same shape probing a nonexistent page id would produce.
        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Empty(registry.GetViewers(page.Id));
    }

    [Fact]
    public async Task PointerMove_IsDeliveredToOthersInGroup_NotToSelf()
    {
        var (_, page) = await SeedViewablePageAsync();
        await using var mover = await ConnectAsync($"mover-{Guid.NewGuid()}");
        await using var observer = await ConnectAsync($"observer-{Guid.NewGuid()}");

        var moverGotPointer = new TaskCompletionSource<bool>();
        mover.On<object>("PointerMoved", _ => moverGotPointer.TrySetResult(true));
        var observerPointer = new TaskCompletionSource<System.Text.Json.JsonElement>();
        observer.On<System.Text.Json.JsonElement>("PointerMoved", payload => observerPointer.TrySetResult(payload));

        await mover.InvokeAsync("JoinPage", page.Id);
        await observer.InvokeAsync("JoinPage", page.Id);
        await mover.InvokeAsync("PointerMove", page.Id, 12.5, 34.5);

        var payload = await observerPointer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(12.5, payload.GetProperty("x").GetDouble());

        // Give the "did mover receive its own broadcast" negative case a fair window
        // before asserting it never arrives.
        var completed = await Task.WhenAny(moverGotPointer.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(moverGotPointer.Task, completed);
    }

    [Fact]
    public async Task LeavePage_RemovesPresence_AndBroadcastsUpdatedViewersChanged()
    {
        var (_, page) = await SeedViewablePageAsync();
        await using var connection = await ConnectAsync($"leaver-{Guid.NewGuid()}");

        var viewersChangedEvents = new List<object[]>();
        var secondBroadcast = new TaskCompletionSource<bool>();
        connection.On<object[]>("ViewersChanged", views =>
        {
            viewersChangedEvents.Add(views);
            if (viewersChangedEvents.Count == 2)
            {
                secondBroadcast.TrySetResult(true);
            }
        });

        await connection.InvokeAsync("JoinPage", page.Id);
        await connection.InvokeAsync("LeavePage", page.Id);
        await secondBroadcast.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Empty(registry.GetViewers(page.Id));
    }

    [Fact]
    public async Task RuleChange_EvictsNowRestrictedConnection_FromPresenceGroup()
    {
        var (_, page) = await SeedViewablePageAsync();
        await using var connection = await ConnectAsync($"about-to-be-restricted-{Guid.NewGuid()}");
        await connection.InvokeAsync("JoinPage", page.Id);

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers(page.Id));

        // Now restrict the page to a nationality this connection doesn't hold, via the
        // real createAccessRule mutation (as an instance admin, to exercise the actual
        // resolver-to-notifier wiring rather than calling IPresenceRuleChangeNotifier
        // directly).
        var adminClient = factory.CreateClient();
        adminClient.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);
        var mutationResult = await adminClient.PostGraphQLAsync($$"""
            mutation {
              createAccessRule(input: {
                kind: PAGE_RESTRICTION, spaceId: null, pageId: "{{page.Id}}", role: null, action: VIEW,
                expressionJson: {{System.Text.Json.JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])))}}
              }) { rule { id } error { kind } }
            }
            """);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            mutationResult.RootElement.GetProperty("data").GetProperty("createAccessRule").GetProperty("error").ValueKind);

        // Give the notifier's async broadcast a moment to run - it's awaited inside the
        // mutation resolver itself (see Mutation.AccessRules.cs), so by the time the
        // HTTP response above returned, eviction has already happened synchronously;
        // this is just guarding against any residual async scheduling.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Empty(registry.GetViewers(page.Id));
    }
}
