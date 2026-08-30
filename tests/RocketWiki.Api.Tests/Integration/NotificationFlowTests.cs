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
/// Recipients with an open hub connection get send-time canView plus a live push, so
/// the connected-path tests connect first — which also exercises the full wire
/// contract the frontend shipped (web/src/realtime/types.ts NotificationPayload).
/// Recipients WITHOUT a connection get the deferred path (INotificationDispatcher):
/// a row with no TitleSnapshot whose existence and title are resolved at the
/// notifications fetch, exactly like SyncImported rows — the offline-recipient tests
/// below deliberately never connect.
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

        var space = new Space { Key = $"NF{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Notification Flow Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
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

    private async Task EditPageAsync(HttpClient editor, Guid pageId, int expectedRevision, string content, string title = "Watched Page")
    {
        using var result = await editor.PostGraphQLAsync($$"""
            mutation {
              updatePageContent(input: {
                pageId: "{{pageId}}", expectedRevisionNumber: {{expectedRevision}},
                title: {{JsonSerializer.Serialize(title)}}, content: {{JsonSerializer.Serialize(content)}}, editSummary: null
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

    private async Task<string> AddCommentAsync(HttpClient client, Guid pageId, string body, string? parentCommentId = null)
    {
        var parentField = parentCommentId is null ? "" : $"parentCommentId: \"{parentCommentId}\", ";
        using var result = await client.PostGraphQLAsync($$"""
            mutation { addComment(input: { pageId: "{{pageId}}", {{parentField}}body: {{JsonSerializer.Serialize(body)}} }) { comment { id } error { kind } } }
            """);
        var data = result.RootElement.GetProperty("data").GetProperty("addComment");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("error").ValueKind);
        return data.GetProperty("comment").GetProperty("id").GetString()!;
    }

    private async Task EditCommentAsync(HttpClient client, string commentId, string body)
    {
        using var result = await client.PostGraphQLAsync($$"""
            mutation { editComment(input: { commentId: "{{commentId}}", body: {{JsonSerializer.Serialize(body)}} }) { comment { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("editComment").GetProperty("error").ValueKind);
    }

    // --- comment_reply (design.md §8: "someone replied to your comment") --------------

    [Fact]
    public async Task ReplyToComment_NotifiesParentAuthor_WithCommentReply()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var parentAuthorSub = $"parent-author-{Guid.NewGuid()}";
        var parentAuthorId = await ProvisionUserAsync(parentAuthorSub);
        var parentAuthorClient = AuthedClient(parentAuthorSub);
        var parentCommentId = await AddCommentAsync(parentAuthorClient, pageId, "First!");

        await using var connection = await ConnectAsync(parentAuthorSub);
        var pushed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("Notification", payload => pushed.TrySetResult(payload));

        var replierSub = $"replier-{Guid.NewGuid()}";
        await ProvisionUserAsync(replierSub);
        await AddCommentAsync(AuthedClient(replierSub), pageId, "Good point.", parentCommentId);

        var row = Assert.Single(await RowsForAsync(parentAuthorId));
        Assert.Equal(NotificationType.CommentReply, row.Type);
        Assert.Equal(pageId, row.PageId);

        var payload = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("comment_reply", payload.GetProperty("type").GetString());
        Assert.Equal(replierSub, payload.GetProperty("actorDisplayName").GetString());
    }

    [Fact]
    public async Task ReplyToOwnComment_ProducesNothing()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var authorSub = $"self-replier-{Guid.NewGuid()}";
        var authorId = await ProvisionUserAsync(authorSub);
        await using var connection = await ConnectAsync(authorSub);

        var client = AuthedClient(authorSub);
        var parentCommentId = await AddCommentAsync(client, pageId, "Talking...");
        await AddCommentAsync(client, pageId, "...to myself.", parentCommentId);

        Assert.Empty(await RowsForAsync(authorId));
    }

    [Fact]
    public async Task ReplyThatAlsoMentionsParentAuthor_YieldsOneMention_NotReplyPlusMention()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var parentAuthorSub = $"both-{Guid.NewGuid()}";
        var parentAuthorId = await ProvisionUserAsync(parentAuthorSub);
        var parentCommentId = await AddCommentAsync(AuthedClient(parentAuthorSub), pageId, "Original comment.");
        await using var connection = await ConnectAsync(parentAuthorSub);

        var replierSub = $"replier2-{Guid.NewGuid()}";
        await ProvisionUserAsync(replierSub);
        await AddCommentAsync(AuthedClient(replierSub), pageId,
            $"Agreed, @[{parentAuthorSub}](user://{parentAuthorId}).", parentCommentId);

        // One event, one notification, the most specific type wins - the same
        // precedence rule as mention-beats-watch on page saves.
        var row = Assert.Single(await RowsForAsync(parentAuthorId));
        Assert.Equal(NotificationType.Mention, row.Type);
    }

    [Fact]
    public async Task ReplyWhenParentAuthorLostCanView_ProducesNothing()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var parentAuthorSub = $"nz-parent-{Guid.NewGuid()}";
        var parentAuthorId = await ProvisionUserAsync(parentAuthorSub, nationality: ["NZ"]);
        var parentCommentId = await AddCommentAsync(AuthedClient(parentAuthorSub, nationality: ["NZ"]), pageId, "Commented while viewable.");

        await using var connection = await ConnectAsync(parentAuthorSub, nationality: ["NZ"]);
        var pushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("Notification", _ => pushed.TrySetResult(true));

        // The page becomes US-only AFTER the parent comment exists - canView at
        // send time (design.md §8) must now silence the reply notification entirely.
        await RestrictPageViewToUsAsync(pageId);

        var replierSub = $"us-replier-{Guid.NewGuid()}";
        await ProvisionUserAsync(replierSub, nationality: ["US"]);
        await AddCommentAsync(AuthedClient(replierSub, nationality: ["US"]), pageId, "US-only reply.", parentCommentId);

        Assert.Empty(await RowsForAsync(parentAuthorId));
        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(pushed.Task, completed);
    }

    [Fact]
    public async Task CommentActivity_DoesNotNotifyPageWatchers()
    {
        // Deliberate, preserved behavior: page_watched_changed means the page's CONTENT
        // changed. Comment activity has never fanned out to watchers, and adding the
        // reply producer must not change that.
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"comment-watcher-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub);
        var watcherClient = AuthedClient(watcherSub);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        await using var connection = await ConnectAsync(watcherSub);

        var commenterSub = $"commenter2-{Guid.NewGuid()}";
        await ProvisionUserAsync(commenterSub);
        var parentId = await AddCommentAsync(AuthedClient(commenterSub), pageId, "No watcher ping for this.");

        var replierSub = $"replier3-{Guid.NewGuid()}";
        await ProvisionUserAsync(replierSub);
        await AddCommentAsync(AuthedClient(replierSub), pageId, "Nor this reply.", parentId);

        Assert.Empty(await RowsForAsync(watcherId));
    }

    // --- editComment: delta-based mention re-scan (design.md §8) ----------------------

    [Fact]
    public async Task EditComment_NotifiesOnlyNewlyMentionedUsers()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var standingSub = $"standing-{Guid.NewGuid()}";
        var standingId = await ProvisionUserAsync(standingSub);
        var newSub = $"newly-{Guid.NewGuid()}";
        var newId = await ProvisionUserAsync(newSub);
        await using var standingConnection = await ConnectAsync(standingSub);
        await using var newConnection = await ConnectAsync(newSub);

        var authorSub = $"editing-author-{Guid.NewGuid()}";
        await ProvisionUserAsync(authorSub);
        var authorClient = AuthedClient(authorSub);
        var commentId = await AddCommentAsync(authorClient, pageId, $"cc @[{standingSub}](user://{standingId})");
        Assert.Single(await RowsForAsync(standingId)); // mentioned on add

        // The edit ADDS a mention of newSub while keeping standingSub's - only the
        // NEW mention notifies (delta against the pre-edit body, same rule as pages).
        await EditCommentAsync(authorClient, commentId,
            $"cc @[{standingSub}](user://{standingId}) and now @[{newSub}](user://{newId})");

        Assert.Single(await RowsForAsync(standingId)); // NOT re-pinged
        var newRow = Assert.Single(await RowsForAsync(newId));
        Assert.Equal(NotificationType.Mention, newRow.Type);
        Assert.Equal(pageId, newRow.PageId);

        // An edit that changes no mentions notifies nobody at all.
        await EditCommentAsync(authorClient, commentId,
            $"cc @[{standingSub}](user://{standingId}) and now @[{newSub}](user://{newId}) (typo fix)");
        Assert.Single(await RowsForAsync(standingId));
        Assert.Single(await RowsForAsync(newId));
    }

    // --- sync_bundle_landed rows surfacing through the notifications query ------------
    // The PRODUCER runs in the offline Sync CLI (BundleImportService, covered by
    // RocketWiki.Data.Tests.SyncBundleNotificationTests); what this proves end-to-end
    // is the read side those rows depend on: the deferred canView check against the
    // caller's live token, and the shipped wire vocabulary.

    private async Task<(Guid SpaceId, Guid PageId)> SeedReplicaPageWithSyncRowsAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        // OriginInstanceId != "standalone" (the API's default Instance:Id) -> replica.
        var space = new Space { Key = $"RS{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Replica", OriginInstanceId = "low-side", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "landed", Title = "Landed Replica Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.Pages.Add(page);

        // Exactly what BundleImportService writes: no snapshot, no actor.
        db.Notifications.Add(new Notification
        {
            RecipientUserId = recipientUserId, Type = NotificationType.SyncImported,
            PageId = page.Id, SpaceId = space.Id, TitleSnapshot = null, CreatedAtUtc = DateTime.UtcNow,
        });
        db.Notifications.Add(new Notification
        {
            RecipientUserId = recipientUserId, Type = NotificationType.SyncImported,
            PageId = null, SpaceId = space.Id, TitleSnapshot = null, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (space.Id, page.Id);
    }

    [Fact]
    public async Task SyncBundleLandedRows_SurfaceWithLiveTitle_ForRecipientWhoCanViewNow()
    {
        var recipientSub = $"sync-recipient-{Guid.NewGuid()}";
        var recipientId = await ProvisionUserAsync(recipientSub);
        var (_, pageId) = await SeedReplicaPageWithSyncRowsAsync(recipientId);

        using var list = await AuthedClient(recipientSub).PostGraphQLAsync(
            "query { notifications { id type pageId spaceKey pageTitle actorDisplayName } }");
        var items = list.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("sync_bundle_landed", i.GetProperty("type").GetString()));
        Assert.All(items, i => Assert.Equal("System", i.GetProperty("actorDisplayName").GetString()));

        var pageScoped = Assert.Single(items, i => i.GetProperty("pageId").ValueKind != JsonValueKind.Null);
        Assert.Equal(pageId.ToString(), pageScoped.GetProperty("pageId").GetString());
        // The row carries no snapshot; canView passes NOW, so the live title serves.
        Assert.Equal("Landed Replica Page", pageScoped.GetProperty("pageTitle").GetString());

        var spaceScoped = Assert.Single(items, i => i.GetProperty("pageId").ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, spaceScoped.GetProperty("pageTitle").ValueKind);
        Assert.False(string.IsNullOrEmpty(spaceScoped.GetProperty("spaceKey").GetString()));
    }

    [Fact]
    public async Task SyncBundleLandedRow_IsFullySuppressed_WhenPageCanViewFailsNow()
    {
        var recipientSub = $"nz-sync-{Guid.NewGuid()}";
        var recipientId = await ProvisionUserAsync(recipientSub, nationality: ["NZ"]);
        var (_, pageId) = await SeedReplicaPageWithSyncRowsAsync(recipientId);
        await RestrictPageViewToUsAsync(pageId);

        using var list = await AuthedClient(recipientSub, nationality: ["NZ"]).PostGraphQLAsync(
            "query { notifications { id type pageId pageTitle } }");
        var items = list.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray().ToList();

        // These rows never passed canView at send time (written offline by the sync
        // CLI), so a failed read-time check suppresses the ROW, not just the title -
        // the page-scoped one must vanish entirely. The space-scoped row survives on
        // its own gate: the recipient still holds a space role (the everyone grant),
        // and it names no page.
        var survivor = Assert.Single(items);
        Assert.Equal("sync_bundle_landed", survivor.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, survivor.GetProperty("pageId").ValueKind);
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

    // --- offline recipients: the deferred path (INotificationDispatcher) --------------
    // No hub connection means no live Principal, so no canView can run at send time.
    // The dispatcher writes a row with TitleSnapshot null - nothing disclosed - and
    // the notifications fetch is both the authorization check and the delivery,
    // exactly like SyncImported rows (design.md §8). These recipients NEVER connect.

    [Fact]
    public async Task OfflineWatcher_GetsDeferredRow_SurfacedOnFetchWithLiveTitle()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"offline-watcher-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub);
        var watcherClient = AuthedClient(watcherSub);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchPage").GetProperty("error").ValueKind);
        // Deliberately no ConnectAsync: the watcher is offline for the whole event.

        var actorSub = $"offline-editor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        var actorClient = AuthedClient(actorSub);
        await EditPageAsync(actorClient, pageId, expectedRevision: 0, content: "Edited while watcher offline.");
        // A second edit renames the page - proving the fetch serves the LIVE title,
        // not anything captured at send time.
        await EditPageAsync(actorClient, pageId, expectedRevision: 1, content: "Renamed too.", title: "Renamed While Watcher Away");

        // The persisted rows are blind: no snapshot, because no principal existed to
        // authorize any disclosure at send time.
        var rows = await RowsForAsync(watcherId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(NotificationType.PageUpdated, r.Type));
        Assert.All(rows, r => Assert.Null(r.TitleSnapshot));
        Assert.All(rows, r => Assert.Equal(pageId, r.PageId));

        // The fetch is the delivery: existence gate passes (the watcher can view), and
        // the title is the page's CURRENT one.
        using var list = await watcherClient.PostGraphQLAsync(
            "query { notifications { id type pageId pageTitle actorDisplayName readAtUtc } }");
        var items = list.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("page_watched_changed", i.GetProperty("type").GetString()));
        Assert.All(items, i => Assert.Equal("Renamed While Watcher Away", i.GetProperty("pageTitle").GetString()));
        Assert.All(items, i => Assert.Equal(pageId.ToString(), i.GetProperty("pageId").GetString()));
        Assert.All(items, i => Assert.Equal(actorSub, i.GetProperty("actorDisplayName").GetString()));

        // Mark-read works for a deferred row the caller can view - the receipt carries
        // id + readAtUtc but withholds the subject (mark-read is a receipt, the list
        // is the disclosure surface).
        var notificationId = items[0].GetProperty("id").GetString();
        using var mark = await watcherClient.PostGraphQLAsync($$"""
            mutation { markNotificationRead(input: { notificationId: "{{notificationId}}" }) { notification { id pageId pageTitle readAtUtc } error { kind } } }
            """);
        var marked = mark.RootElement.GetProperty("data").GetProperty("markNotificationRead");
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("error").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, marked.GetProperty("notification").GetProperty("readAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("notification").GetProperty("pageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("notification").GetProperty("pageTitle").ValueKind);
    }

    [Fact]
    public async Task OfflineWatcherWithoutCanView_FetchesNothing_AndMarkReadProbeRevealsNothing()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var watcherSub = $"nz-offline-{Guid.NewGuid()}";
        var watcherId = await ProvisionUserAsync(watcherSub, nationality: ["NZ"]);
        var watcherClient = AuthedClient(watcherSub, nationality: ["NZ"]);
        using var watchResult = await watcherClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchPage").GetProperty("error").ValueKind);

        // Access lost while offline; then the page changes.
        await RestrictPageViewToUsAsync(pageId);
        var actorSub = $"us-offline-editor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub, nationality: ["US"]);
        await EditPageAsync(AuthedClient(actorSub, nationality: ["US"]), pageId, expectedRevision: 0, content: "US-only while watcher offline.");

        // The blind row exists (written with no authorization decision, none possible)...
        var row = Assert.Single(await RowsForAsync(watcherId));
        Assert.Null(row.TitleSnapshot);

        // ...but the recipient must never learn it does: the fetch runs the deferred
        // check against their live Principal and suppresses the row's EXISTENCE, the
        // same fail-closed shape as SyncImported rows (design.md §8).
        using var list = await watcherClient.PostGraphQLAsync("query { notifications { id } }");
        Assert.Equal(0, list.RootElement.GetProperty("data").GetProperty("notifications").GetArrayLength());

        // Nor may markNotificationRead become the probe around that suppression: the
        // row id (sequential bigint) yields NotFound - indistinguishable from a
        // nonexistent id - with no receipt, no type, no subject. And the row stays
        // unread, so it surfaces intact if access is ever restored.
        using var mark = await watcherClient.PostGraphQLAsync($$"""
            mutation { markNotificationRead(input: { notificationId: "{{row.Id}}" }) { notification { id readAtUtc } error { kind } } }
            """);
        var marked = mark.RootElement.GetProperty("data").GetProperty("markNotificationRead");
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("notification").ValueKind);
        Assert.Equal("NotFound", marked.GetProperty("error").GetProperty("kind").GetString());
        Assert.Null(Assert.Single(await RowsForAsync(watcherId)).ReadAtUtc);
    }

    [Fact]
    public async Task OfflineMentionedUser_GetsDeferredMentionRow_SurfacedOnFetch()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var mentionedSub = $"offline-mentioned-{Guid.NewGuid()}";
        var mentionedId = await ProvisionUserAsync(mentionedSub);
        // No connection, no watch - the mention alone is the candidacy.

        var actorSub = $"mentioning-author-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0,
            content: $"Heads up, @[{mentionedSub}](user://{mentionedId}).");

        var row = Assert.Single(await RowsForAsync(mentionedId));
        Assert.Equal(NotificationType.Mention, row.Type);
        Assert.Null(row.TitleSnapshot);

        using var list = await AuthedClient(mentionedSub).PostGraphQLAsync(
            "query { notifications { type pageId pageTitle } }");
        var item = Assert.Single(list.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray());
        Assert.Equal("mention", item.GetProperty("type").GetString());
        Assert.Equal(pageId.ToString(), item.GetProperty("pageId").GetString());
        Assert.Equal("Watched Page", item.GetProperty("pageTitle").GetString()); // live title
    }

    [Fact]
    public async Task OfflineWatcherWhoIsAlsoMentioned_GetsOneDeferredRow_OfTypeMention()
    {
        // The most-specific-type-wins precedence (mention beats watch) is resolved
        // over the WHOLE candidate set before connectivity splits it, so it must hold
        // on the deferred half too: one event, one row, type Mention.
        var (_, pageId) = await SeedEditablePageAsync();
        var bothSub = $"offline-both-{Guid.NewGuid()}";
        var bothId = await ProvisionUserAsync(bothSub);
        var bothClient = AuthedClient(bothSub);
        using var watchResult = await bothClient.PostGraphQLAsync($$"""
            mutation { watchPage(input: { pageId: "{{pageId}}" }) { watch { id } error { kind } } }
            """);
        Assert.Equal(JsonValueKind.Null,
            watchResult.RootElement.GetProperty("data").GetProperty("watchPage").GetProperty("error").ValueKind);

        var actorSub = $"both-editor-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0,
            content: $"Watched AND mentioned: @[{bothSub}](user://{bothId}).");

        var row = Assert.Single(await RowsForAsync(bothId));
        Assert.Equal(NotificationType.Mention, row.Type);
        Assert.Null(row.TitleSnapshot);
    }

    [Fact]
    public async Task ReplyToOfflineParentAuthor_WritesDeferredCommentReplyRow_SurfacedOnFetch()
    {
        var (_, pageId) = await SeedEditablePageAsync();
        var parentAuthorSub = $"offline-parent-{Guid.NewGuid()}";
        var parentAuthorId = await ProvisionUserAsync(parentAuthorSub);
        var parentCommentId = await AddCommentAsync(AuthedClient(parentAuthorSub), pageId, "Posted, then went home.");
        // The parent author never connects to the hub.

        var replierSub = $"evening-replier-{Guid.NewGuid()}";
        await ProvisionUserAsync(replierSub);
        await AddCommentAsync(AuthedClient(replierSub), pageId, "Replying after hours.", parentCommentId);

        var row = Assert.Single(await RowsForAsync(parentAuthorId));
        Assert.Equal(NotificationType.CommentReply, row.Type);
        Assert.Null(row.TitleSnapshot);
        Assert.Equal(pageId, row.PageId);

        using var list = await AuthedClient(parentAuthorSub).PostGraphQLAsync(
            "query { notifications { type pageTitle actorDisplayName } }");
        var item = Assert.Single(list.RootElement.GetProperty("data").GetProperty("notifications").EnumerateArray());
        Assert.Equal("comment_reply", item.GetProperty("type").GetString());
        Assert.Equal("Watched Page", item.GetProperty("pageTitle").GetString());
        Assert.Equal(replierSub, item.GetProperty("actorDisplayName").GetString());
    }

    [Fact]
    public async Task MentionOfNonexistentUserId_ProducesNoRow_AndDoesNotFailTheSave()
    {
        // Mention ids are parsed from user-typed Markdown; an id naming no local User
        // is always "offline", and blindly writing its deferred row would be an FK
        // violation failing the whole mutation. It must simply produce nothing.
        var (_, pageId) = await SeedEditablePageAsync();
        var ghostId = Guid.NewGuid();

        var actorSub = $"ghost-author-{Guid.NewGuid()}";
        await ProvisionUserAsync(actorSub);
        await EditPageAsync(AuthedClient(actorSub), pageId, expectedRevision: 0,
            content: $"Ping to nobody: @[ghost](user://{ghostId})."); // EditPageAsync asserts the save succeeded

        Assert.Empty(await RowsForAsync(ghostId));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Empty(await db.Notifications.AsNoTracking().Where(n => n.PageId == pageId).ToListAsync());
    }
}
