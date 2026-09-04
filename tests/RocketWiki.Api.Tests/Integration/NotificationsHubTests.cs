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

        var space = new Space { Key = $"HB{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Hub Test Space", OriginInstanceId = "standalone", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id };
        db.Spaces.Add(space);
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        }));
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
        Assert.Single(registry.GetViewers($"page:{page.Id}"));
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
        Assert.Empty(registry.GetViewers($"page:{page.Id}"));
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
        Assert.Empty(registry.GetViewers($"page:{page.Id}"));
    }

    /// <summary>
    /// Asserts BOTH halves of an eviction, because they are separate calls and only one of
    /// them stops data reaching the client. The registry is emptied by
    /// <c>registry.LeavePage</c>; the SignalR group is left by
    /// <c>hubContext.Groups.RemoveFromGroupAsync</c>. This test used to assert the registry
    /// half only — so deleting the group removal, the line that actually closes the
    /// channel, left it green while an evicted client carried on receiving everything
    /// broadcast to the page. Its co-edit twin (EditSessionHubTests) already asserted the
    /// relay closes; this is the presence side catching up.
    /// </summary>
    [Fact]
    public async Task RuleChange_EvictsNowRestrictedConnection_FromPresenceGroup()
    {
        var (space, page) = await SeedViewablePageAsync();
        await using var connection = await ConnectAsync($"about-to-be-restricted-{Guid.NewGuid()}");

        // Everything the page group sends this connection AFTER the eviction is a failure;
        // captured from the start so there is no subscribe-too-late window.
        var afterEviction = new List<object?[]>();
        connection.On<object?[]>("ViewersChanged", payload =>
        {
            lock (afterEviction)
            {
                afterEviction.Add(payload);
            }
        });

        await connection.InvokeAsync("JoinPage", page.Id);

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers($"page:{page.Id}"));

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

        Assert.Empty(registry.GetViewers($"page:{page.Id}"));

        // The half the registry assertion cannot see: the connection must no longer be a
        // MEMBER of the page group. Provoke a broadcast to that group by having someone
        // else join the page, then assert the evicted connection heard nothing.
        lock (afterEviction)
        {
            afterEviction.Clear();
        }

        await using var newcomer = await ConnectAsync($"still-allowed-{Guid.NewGuid()}", nationality: ["US"]);
        await newcomer.InvokeAsync("JoinPage", page.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        lock (afterEviction)
        {
            Assert.Empty(afterEviction);
        }

        // Non-vacuous: the newcomer IS in the group, so the broadcast really happened.
        Assert.Single(registry.GetViewers($"page:{page.Id}"));
        _ = space;
    }

    /// <summary>
    /// design.md §8's eviction promise applied to the input it was missing. <c>canView</c>
    /// is computed from space grants, the restriction chain <b>and the protective marking</b>
    /// (§21), but the sweep was called from the access-rule mutations only — so a page
    /// re-marked out of a joined viewer's reach left them in the SignalR group.
    ///
    /// <para>For a co-editor that means still receiving <c>UpdateReceived</c>, which is
    /// page content in CRDT form, on a page they can no longer read. §21 is explicit that a
    /// marking gates views "on every read path, exactly as a page restriction does"; a live
    /// session is a read path that keeps delivering.</para>
    ///
    /// <para>Driven through the real <c>setPageMarking</c> mutation rather than by calling
    /// the notifier, because the defect was precisely that the resolver never called it.</para>
    /// </summary>
    [Fact]
    public async Task MarkingChange_EvictsAViewerTheMarkingNoLongerAdmits()
    {
        var (_, page) = await SeedViewablePageAsync();

        // No nationality claim, so this viewer holds nothing an eyes-only caveat could
        // match (§21.4). (This used to be "no clearance, so OFFICIAL"; the level gates
        // nobody now, so the caveat is the marking change that can evict.)
        await using var connection = await ConnectAsync($"about-to-be-excluded-{Guid.NewGuid()}");
        await connection.InvokeAsync("JoinPage", page.Id);

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers($"page:{page.Id}"));

        // A UK-national editor marks the page UK EYES ONLY. Nothing about the access
        // RULES changes - only the marking - which is exactly the case the sweep used to miss.
        var editorClient = factory.CreateClient();
        editorClient.SetTestUser(sub: $"uk-editor-{Guid.NewGuid()}", roles: ["admin"], nationality: ["UK"]);
        var mutationResult = await editorClient.PostGraphQLAsync($$"""
            mutation {
              setPageMarking(input: { pageId: "{{page.Id}}", level: SECRET, eyesOnly: [UK], selectors: [], ukPrefix: true }) {
                marking { level }
                error { kind message }
              }
            }
            """);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            mutationResult.RootElement.GetProperty("data").GetProperty("setPageMarking").GetProperty("error").ValueKind);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Empty(registry.GetViewers($"page:{page.Id}"));
    }

    /// <summary>
    /// Seeds a space whose only grant requires the given nationality, so a caller without
    /// it holds no role and the space is invisible to them.
    /// </summary>
    private async Task<Space> SeedSpaceAsync(string grantNationality)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"RM{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Room Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", [grantNationality])),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = seeder.Id,
        });
        await db.SaveChangesAsync();
        return space;
    }

    [Fact]
    public async Task JoinRoom_OnAViewableSpace_RegistersPresence()
    {
        // The control for the two refusal tests below: without it, "nothing registered"
        // could mean the space room never works at all.
        var space = await SeedSpaceAsync(grantNationality: "GB");
        await using var connection = await ConnectAsync($"gb-{Guid.NewGuid()}", nationality: ["GB"]);

        var viewersChanged = new TaskCompletionSource<object[]>();
        connection.On<object[]>("ViewersChanged", views => viewersChanged.TrySetResult(views));

        await connection.InvokeAsync("JoinRoom", $"space:{space.Key}:browse");
        var views = await viewersChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(views);
        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers($"space:{space.Key}:browse"));
    }

    [Fact]
    public async Task JoinRoom_OnAnInvisibleSpace_IsIndistinguishableFromANonexistentOne()
    {
        // §6.7's line for this feature. A space room must never become a way to learn
        // that a space EXISTS: the refusal for a space the caller cannot see has to be
        // byte-identical to the refusal for one that was never created.
        //
        // Both calls are made on the same connection, so anything that differed —
        // an exception, an event, a delay-shaped difference in what the client observes —
        // would show up as an asymmetry between these two halves.
        var invisible = await SeedSpaceAsync(grantNationality: "US");
        var neverExisted = $"NX{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var connection = await ConnectAsync($"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        var events = new List<object[]>();
        connection.On<object[]>("ViewersChanged", views =>
        {
            lock (events)
            {
                events.Add(views);
            }
        });

        var invisibleFault = await Record.ExceptionAsync(
            () => connection.InvokeAsync("JoinRoom", $"space:{invisible.Key}:browse"));
        var absentFault = await Record.ExceptionAsync(
            () => connection.InvokeAsync("JoinRoom", $"space:{neverExisted}:browse"));

        // Same outcome on the wire: no fault either time.
        Assert.Null(invisibleFault);
        Assert.Null(absentFault);

        // Same outcome in the registry: nothing joined either room.
        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Empty(registry.GetViewers($"space:{invisible.Key}:browse"));
        Assert.Empty(registry.GetViewers($"space:{neverExisted}:browse"));

        // And no broadcast escaped for either — a ViewersChanged naming the invisible
        // space would confirm its existence just as loudly as an error would.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        lock (events)
        {
            Assert.Empty(events);
        }
    }

    [Fact]
    public async Task JoinRoom_OnAnAllowlistedSiteRoute_RegistersPresenceForAnyAuthenticatedUser()
    {
        // A global route carries no resource id, so being signed in is the whole gate —
        // and that is only safe because the route came off a fixed allowlist.
        await using var connection = await ConnectAsync($"anyone-{Guid.NewGuid()}");

        var viewersChanged = new TaskCompletionSource<object[]>();
        connection.On<object[]>("ViewersChanged", views => viewersChanged.TrySetResult(views));

        await connection.InvokeAsync("JoinRoom", "site:/search");
        await viewersChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers("site:/search"));
    }

    [Fact]
    public async Task JoinRoom_OnAnUnknownRoomKey_RegistersNothing()
    {
        // The refusal that keeps the room key space bounded, and the one that stops an
        // unrecognised prefix becoming a room with no gate at all.
        await using var connection = await ConnectAsync($"prober-{Guid.NewGuid()}");
        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();

        foreach (var key in new[] { "everyone:all", "page:not-a-guid", "nonsense", "site:" })
        {
            var fault = await Record.ExceptionAsync(() => connection.InvokeAsync("JoinRoom", key));
            Assert.Null(fault);
            Assert.Empty(registry.GetViewers(key));
        }
    }

    [Fact]
    public async Task JoinPage_StillWorks_AndLandsInTheSameRoomAsJoinRoom()
    {
        // The transitional adapter. The SPA still calls JoinPage while it moves to rooms,
        // and it must land in the room JoinRoom would have produced — otherwise the two
        // clients see each other's absence during the changeover.
        var (_, page) = await SeedViewablePageAsync();

        await using var legacy = await ConnectAsync($"legacy-{Guid.NewGuid()}");
        await using var modern = await ConnectAsync($"modern-{Guid.NewGuid()}");

        await legacy.InvokeAsync("JoinPage", page.Id);
        await modern.InvokeAsync("JoinRoom", $"page:{page.Id}");

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Equal(2, registry.GetViewers($"page:{page.Id}").Count);
    }

    [Fact]
    public async Task TwoScreensOfOneSpace_ShareAuthorizationButAreSeparateRooms()
    {
        // The contract the SPA sends. Both screens need the same permission, so one
        // authorization decision covers both — but a cursor position on the browser
        // means nothing on the trash screen, so a viewer in one must not appear in the
        // other. Getting this wrong in the other direction is what my first cut did:
        // it authorized against "ENG:browse" as if that were a space key, and every
        // space room was refused for everyone.
        var space = await SeedSpaceAsync(grantNationality: "GB");

        await using var onBrowse = await ConnectAsync($"browse-{Guid.NewGuid()}", nationality: ["GB"]);
        await using var onTrash = await ConnectAsync($"trash-{Guid.NewGuid()}", nationality: ["GB"]);

        await onBrowse.InvokeAsync("JoinRoom", $"space:{space.Key}:browse");
        await onTrash.InvokeAsync("JoinRoom", $"space:{space.Key}:trash");

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();

        // Both authorized: the gate read the space key, not the screen.
        Assert.Single(registry.GetViewers($"space:{space.Key}:browse"));
        Assert.Single(registry.GetViewers($"space:{space.Key}:trash"));

        // And separate: neither sees the other.
        Assert.DoesNotContain(registry.GetViewers($"space:{space.Key}:browse"),
            v => v.ConnectionId == registry.GetViewers($"space:{space.Key}:trash")[0].ConnectionId);
    }

    [Fact]
    public async Task DocsTopics_AreDistinctRooms_AndNeedNoBackendRouteList()
    {
        // The case an allowlist could never serve: help topics are open-ended, so any
        // server-side route list would have refused them and docs presence would simply
        // be dead. A site path names a screen, not a resource — nothing to authorize,
        // nothing to leak — so any well-formed path from a signed-in caller joins, and
        // two topics are two rooms.
        await using var onClassification = await ConnectAsync($"docs-a-{Guid.NewGuid()}");
        await using var onOrganising = await ConnectAsync($"docs-b-{Guid.NewGuid()}");

        await onClassification.InvokeAsync("JoinRoom", "site:/-/docs/classification");
        await onOrganising.InvokeAsync("JoinRoom", "site:/-/docs/organising");

        var registry = factory.Services.GetRequiredService<IRealtimeConnectionRegistry>();
        Assert.Single(registry.GetViewers("site:/-/docs/classification"));
        Assert.Single(registry.GetViewers("site:/-/docs/organising"));
    }
}
