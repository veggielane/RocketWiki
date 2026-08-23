using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §8, milestone 4b end-to-end: watch → edit → persisted Notification row +
/// live hub push for a permitted watcher; NOTHING (no row, no push) for a watcher who
/// has since lost canView — the adversarial case, in the same spirit as
/// PageAdversarialLeakTests; mentions parsed from the saved Markdown
/// (<c>@[display](user://{id})</c>, §4); markNotificationRead round-trip; and unwatch
/// actually stopping the flow. Hub connections use LongPolling for the same
/// TestServer reason NotificationsHubTests documents.
///
/// The dispatcher only evaluates recipients with an open hub connection (the
/// documented offline gap in INotificationDispatcher), so every would-be recipient in
/// these tests connects first — which also means each test exercises the full wire
/// contract the frontend shipped (web/src/realtime/types.ts NotificationPayload).
/// </summary>
public sealed class NotificationFlowTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
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

    private HttpClient AuthedClient(string sub, IEnumerable<string>? nationality = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, name: sub, nationality: nationality);
        return client;
    }

    /// <summary>JIT-provisions <paramref name="sub"/> via any authenticated request, then returns the local User id the hub/dispatcher will use.</summary>
    private async Task<Guid> ProvisionUserAsync(string sub, IEnumerable<string>? nationality = null)
    {
        var client = AuthedClient(sub, nationality);
        using var _ = await client.PostGraphQLAsync("query { me { isAuthenticated } }");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Subject == sub)).Id;
    }

    private async Task<(Guid SpaceId, Guid PageId)> SeedEditablePageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space { Key = $"NF{Guid.NewGuid():N}"[..8], Name = "Notification Flow Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Watched Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return (space.Id, page.Id);
    }

    private async Task RestrictPageViewToUsAsync(Guid pageId)
    {
        var admin = factory.CreateClient();
        admin.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);
        using var result = await admin.PostGraphQLAsync($$"""
            mutation {
              createAccessRule(input: {
                kind: PAGE_RESTRICTION, spaceId: null, pageId: "{{pageId}}", role: null, action: VIEW,
                expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])))}}
              }) { rule { id } error { kind } }
            }
            """);
        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("createAccessRule").GetProperty("error").ValueKind);
    }

    private async Task EditPageAsync(HttpClient editor, Guid pageId, int expectedRevision, string content)
    {
        using var result = await editor.PostGraphQLAsync($$"""
            mutation {
              updatePageContent(input: {
                pageId: "{{pageId}}", expectedRevisionNumber: {{expectedRevision}},
                title: "Watched Page", content: {{JsonSerializer.Serialize(content)}}, editSummary: null
              }) { page { id currentRevisionNumber } error { kind message } }
            }
            """);
        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("updatePageContent").GetProperty("error").ValueKind);
    }

    private async Task<List<Notification>> RowsForAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == recipientUserId)
            .OrderBy(n => n.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task WatchedPageEdit_PersistsRowAndPushes_ToPermittedConnectedWatcher()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"watcher-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub);

        // Watch via the real mutation (canView-gated), then connect.
        var watcherClient = AuthedClient(watcherSub);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id pageId } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchPage").GetProperty("error").ValueKind);

        await using var connection = await ConnectAsync(watcherSub);
        var pushed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("Notification", payload => pushed.TrySetResult(payload));

        var actorSub = $"actor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0, content: "New content.");

        // Persisted row - the record for offline catch-up (design.md §8).
        var rows = await RowsForAsync(watcherId);
        var row = Assert.Single(rows);
        Assert.Equal(NotificationType.PageUpdated, row.Type);
        Assert.Equal(pageId, row.PageId);
        Assert.Equal("Watched Page", row.TitleSnapshot);
        Assert.Null(row.ReadAtUtc);

        // Live push - field names and type vocabulary are the shipped frontend
        // contract (web/src/realtime/types.ts NotificationPayload).
        var payload = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(row.Id.ToString(), payload.GetProperty("id").GetString());
        Assert.Equal("page_watched_changed", payload.GetProperty("type").GetString());
        Assert.Equal(pageId.ToString(), payload.GetProperty("pageId").GetString());
        Assert.Equal("Watched Page", payload.GetProperty("pageTitle").GetString());
        Assert.Equal(actorSub, payload.GetProperty("actorDisplayName").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("readAtUtc").ValueKind);
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("spaceKey").GetString()));
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("timestampUtc").GetString()));
    }

    [Fact]
    public async Task WatcherWhoLostCanView_GetsNeitherRowNorPush()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"nz-watcher-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub, nationality: ["NZ"]);

        // Watch while the page is still viewable...
        var watcherClient = AuthedClient(watcherSub, nationality: ["NZ"]);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchPage").GetProperty("error").ValueKind);

        await using var connection = await ConnectAsync(watcherSub, nationality: ["NZ"]);
        var pushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("Notification", _ => pushed.TrySetResult(true));

        // ...then lose access: the page becomes US-only. The Watch row still exists;
        // canView is re-evaluated per recipient AT SEND TIME (design.md §8), so the
        // subscription must now produce nothing at all.
        await RestrictPageViewToUsAsync(pageId);

        var actorSub = $"us-actor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub, nationality: ["US"]);
        await EditPageAsync(AuthedClient(actorSub, nationality: ["US"]), pageId, expectedRevision: 0, content: "US-only update.");

        Assert.Empty(await RowsForAsync(watcherId));

        // Same fair-window pattern as NotificationsHubTests' PointerMove negative case.
        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(pushed.Task, completed);
    }

    [Fact]
    public async Task MentionInSavedContent_NotifiesMentionedConnectedUser()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var mentionedSub = $"mentioned-{Guid.NewGuid()}";
        var mentionedId = await ProvisionUserAsync(mentionedSub);

        await using var connection = await ConnectAsync(mentionedSub);
        var pushed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("Notification", payload => pushed.TrySetResult(payload));

        var actorSub = $"author-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);

        // The exact serialized form web/src/editor/markdown emits (design.md §4).
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0,
            content: $"Please review, @[{mentionedSub}](user://{mentionedId}).");

        var rows = await RowsForAsync(mentionedId);
        var row = Assert.Single(rows);
        Assert.Equal(NotificationType.Mention, row.Type);
        Assert.Equal(pageId, row.PageId);

        var payload = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("mention", payload.GetProperty("type").GetString());

        // Re-saving the same content must NOT re-notify: only mentions absent from the
        // previous revision count (INotificationDispatcher's new-mentions rule).
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 1,
            content: $"Please review, @[{mentionedSub}](user://{mentionedId}). (typo fix)");
        Assert.Single(await RowsForAsync(mentionedId));
    }

    [Fact]
    public async Task MentionInComment_NotifiesMentionedConnectedUser()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var mentionedSub = $"cmention-{Guid.NewGuid()}";
        var mentionedId = await ProvisionUserAsync(mentionedSub);
        await using var connection = await ConnectAsync(mentionedSub);

        var commenterSub = $"commenter-{Guid.NewGuid()}";
        await ProvisionUserAsync(commenterSub);
        var body = $"cc @[{mentionedSub}](user://{mentionedId})";
        using var result = await AuthedClient(commenterSub).PostGraphQLAsync($$"""
            mutation { addComment(input: { pageId: "{{pageId}}", body: {{JsonSerializer.Serialize(body)}} }) { comment { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("addComment").GetProperty("error").ValueKind);

        var row = Assert.Single(await RowsForAsync(mentionedId));
        Assert.Equal(NotificationType.Mention, row.Type);
        Assert.Equal(pageId, row.PageId);
    }

    [Fact]
    public async Task MentionOfUserWhoCannotView_ProducesNothing()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        await RestrictPageViewToUsAsync(pageId);

        var mentionedSub = $"nz-mentioned-{Guid.NewGuid()}";
        var mentionedId = await ProvisionUserAsync(mentionedSub, nationality: ["NZ"]);
        await using var connection = await ConnectAsync(mentionedSub, nationality: ["NZ"]);
        var pushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("Notification", _ => pushed.TrySetResult(true));

        var actorSub = $"us-author-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub, nationality: ["US"]);
        await EditPageAsync(AuthedClient(actorSub, nationality: ["US"]), pageId, expectedRevision: 0,
            content: $"Secret ping @[{mentionedSub}](user://{mentionedId}).");

        // A mention must not become a side-channel that tells someone a restricted
        // page (or its title) exists - neither row nor push (design.md §8).
        Assert.Empty(await RowsForAsync(mentionedId));
        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(pushed.Task, completed);
    }

    [Fact]
    public async Task NotificationsQuery_AndMarkRead_RoundTrip()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"reader-{Guid.NewGuid()}";
        await ProvisionUserAsync(watcherSub);
        var watcherClient = AuthedClient(watcherSub);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        await using var connection = await ConnectAsync(watcherSub);

        var actorSub = $"editor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0, content: "Readable update.");

        // The shipped PersistedNotifications selection set, verbatim
        // (web/src/graphql/operations/notifications.graphql).
        using var list = await watcherClient.PostGraphQLAsync("""
            query { notifications { id type pageId spaceKey pageTitle actorDisplayName createdAtUtc readAtUtc } }
            """);
        var items = list.RootElement.GetProperty("data").GetProperty("notifications");
        Assert.Equal(1, items.GetArrayLength());
        var item = items[0];
        Assert.Equal("page_watched_changed", item.GetProperty("type").GetString());
        Assert.Equal("Watched Page", item.GetProperty("pageTitle").GetString());
        Assert.Equal(actorSub, item.GetProperty("actorDisplayName").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAtUtc").ValueKind);
        var notificationId = item.GetProperty("id").GetString();

        using var mark = await watcherClient.PostGraphQLAsync($$"""
            mutation { markNotificationRead(input: { notificationId: "{{notificationId}}" }) { notification { id readAtUtc } error { kind } } }
            """);
        var marked = mark.RootElement.GetProperty("data").GetProperty("markNotificationRead");
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("error").ValueKind);
        Assert.Equal(notificationId, marked.GetProperty("notification").GetProperty("id").GetString());
        Assert.NotEqual(JsonValueKind.Null, marked.GetProperty("notification").GetProperty("readAtUtc").ValueKind);

        using var relist = await watcherClient.PostGraphQLAsync("query { notifications { id readAtUtc } }");
        var relisted = relist.RootElement.GetProperty("data").GetProperty("notifications")[0];
        Assert.NotEqual(JsonValueKind.Null, relisted.GetProperty("readAtUtc").ValueKind);
    }

    [Fact]
    public async Task NotificationsQuery_SuppressesTitle_WhenViewWasLostAfterDelivery()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"stale-{Guid.NewGuid()}";
        await ProvisionUserAsync(watcherSub, nationality: ["NZ"]);
        var watcherClient = AuthedClient(watcherSub, nationality: ["NZ"]);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        await using var connection = await ConnectAsync(watcherSub, nationality: ["NZ"]);

        var actorSub = $"editor2-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0, content: "Delivered while viewable.");

        // Row delivered while permitted; NOW the page becomes US-only. data-model.md:
        // "a row written last week may name a page the user can no longer see, and
        // stale titles must not resurface" - the row stays, its title goes.
        await RestrictPageViewToUsAsync(pageId);

        using var list = await watcherClient.PostGraphQLAsync("query { notifications { id pageId pageTitle type } }");
        var item = list.RootElement.GetProperty("data").GetProperty("notifications")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pageTitle").ValueKind);
        Assert.Equal(pageId.ToString(), item.GetProperty("pageId").GetString());
    }

    [Fact]
    public async Task Unwatch_StopsRows()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"unwatcher-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub);
        var watcherClient = AuthedClient(watcherSub);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        await using var connection = await ConnectAsync(watcherSub);

        var actorSub = $"editor3-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        var actorClient = AuthedClient(actorSub);
        await EditPageAsync(actorClient, pageId, expectedRevision: 0, content: "First edit - watched.");
        Assert.Single(await RowsForAsync(watcherId));

        using var unwatchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { unwatchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            unwatchResult.RootElement.GetProperty("data").GetProperty("unwatchPage").GetProperty("error").ValueKind);

        await EditPageAsync(actorClient, pageId, expectedRevision: 1, content: "Second edit - unwatched.");
        Assert.Single(await RowsForAsync(watcherId)); // still just the first row
    }

    [Fact]
    public async Task WatchPage_OnPageTheCallerCannotView_IsRefused()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        await RestrictPageViewToUsAsync(pageId);

        var nzSub = $"nz-{Guid.NewGuid()}";
        await ProvisionUserAsync(nzSub, nationality: ["NZ"]);
        using var result = await AuthedClient(nzSub, nationality: ["NZ"]).PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        var data = result.RootElement.GetProperty("data").GetProperty("watchPage");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("watch").ValueKind);
        Assert.Equal("Forbidden", data.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task WatchUnwatchAndMarkRead_AreAuditedAsUserActions()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"audited-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub);
        var watcherClient = AuthedClient(watcherSub);
        await using var connection = await ConnectAsync(watcherSub);

        using var w = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);

        var actorSub = $"editor5-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0, content: "Audit flow edit.");

        var row = Assert.Single(await RowsForAsync(watcherId));
        using var m = await watcherClient.PostGraphQLAsync($$"""
            mutation { markNotificationRead(input: { notificationId: "{{row.Id}}" }) { notification { id } error { kind } } }
            """);
        using var u = await watcherClient.PostGraphQLAsync($$"""
            mutation { unwatchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);

        // design.md §8: "watch changes are audited as user actions" - and marking a
        // notification read is a mutation like any other (§7). Delivery itself, by
        // contrast, must NOT audit ("delivery is not a content read") - the watcher
        // performed exactly these three actions, so exactly these three write rows.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var actions = await db.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == watcherId && e.Outcome == AuditOutcome.Success)
            .Select(e => e.Action)
            .ToListAsync();
        Assert.Contains("watch.add", actions);
        Assert.Contains("watch.remove", actions);
        Assert.Contains("notification.markRead", actions);
        Assert.DoesNotContain(actions, a => a.StartsWith("page."));
    }

    [Fact]
    public async Task SpaceWatch_CoversEveryPageInTheSpace()
    {
        var (spaceId, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"space-watcher-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub);
        var watcherClient = AuthedClient(watcherSub);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchSpace(input: { spaceId: "{{spaceId}}" }) { watch { id spaceId } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchSpace").GetProperty("error").ValueKind);
        await using var connection = await ConnectAsync(watcherSub);

        var actorSub = $"editor4-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0, content: "Space-watched edit.");

        var row = Assert.Single(await RowsForAsync(watcherId));
        Assert.Equal(NotificationType.PageUpdated, row.Type);
        Assert.Equal(spaceId, row.SpaceId);
    }
}
