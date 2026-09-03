using System.Text;
using System.Text.Json;
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
/// design.md §8 CRDT co-editing over the real hub (LongPolling against
/// WebApplicationFactory, the NotificationsHubTests pattern): join/seed/replay,
/// canEdit gating with §6.7's silent-refusal shape plus §7's denied-audit half,
/// replica refusal (§12), rule-change eviction, seeder re-designation, and the
/// session-scoped audit rows. The "updates" pushed here are arbitrary bytes on
/// purpose - the server is a relay and must treat them as opaque (the whole §8
/// decision), so nothing in these tests depends on real Yjs encoding.
/// </summary>
public sealed class EditSessionHubTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private async Task<HubConnection> ConnectAsync(
        string sub, IEnumerable<string>? nationality = null, IEnumerable<string>? selectorClaims = null)
    {
        var claimsHeader = TestUserHttpClientExtensions.BuildEncodedClaimsHeaderValue(
            sub, name: sub, nationality: nationality, selectorClaims: selectorClaims);

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

    /// <summary>Editor-to-everyone space with one page; optional EDIT restriction and
    /// optional replica origin, the two refusal shapes JoinEditSession must produce.</summary>
    private async Task<(Space Space, Page Page)> SeedPageAsync(
        string? editRestrictionNationality = null, string? originInstanceId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"ES{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Edit Session Space",
            OriginInstanceId = originInstanceId ?? "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        }));
        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Edit Page",
            CurrentContent = "# Edit Page", CurrentRevisionNumber = 0,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        if (editRestrictionNationality is not null)
        {
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction, PageId = page.Id, Action = PageAction.Edit,
                ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", [editRestrictionNationality])),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            });
            await db.SaveChangesAsync();
        }

        return (space, page);
    }

    private List<AuditEvent> AuditRowsFor(Guid pageId, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == action && e.SubjectId == pageId)
            .OrderBy(e => e.Id)
            .ToList();
    }

    // ---------------------------------------------------------------- join/seed/replay

    [Fact]
    public async Task JoinSeedReplay_RoundTrip_RelaysUpdatesAndReplaysLogToLateJoiner()
    {
        var (_, page) = await SeedPageAsync();
        await using var alice = await ConnectAsync($"alice-{Guid.NewGuid()}");

        var aliceJoin = await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.NotNull(aliceJoin);
        Assert.Equal("seeder", aliceJoin!.Role);
        Assert.Equal(0, aliceJoin.BaseRevisionNumber);
        Assert.Empty(aliceJoin.UpdateLog);

        // The seeder builds the Y.Doc client-side from CurrentContent and pushes the
        // encoded seed as its first ordinary update (design.md §8 co-editing).
        var seed = Encoding.UTF8.GetBytes("seed-update");
        await alice.InvokeAsync("PushUpdate", page.Id, seed);

        await using var bob = await ConnectAsync($"bob-{Guid.NewGuid()}");
        var bobGot = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.On<Guid, byte[]>("UpdateReceived", (pid, update) =>
        {
            if (pid == page.Id)
            {
                bobGot.TrySetResult(update);
            }
        });
        var aliceGotOwn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<Guid, byte[]>("UpdateReceived", (_, _) => aliceGotOwn.TrySetResult(true));

        var bobJoin = await bob.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.NotNull(bobJoin);
        Assert.Equal("joiner", bobJoin!.Role);
        Assert.Equal(new[] { seed }, bobJoin.UpdateLog); // late joiner replays the log

        var update2 = Encoding.UTF8.GetBytes("second-update");
        await alice.InvokeAsync("PushUpdate", page.Id, update2);
        Assert.Equal(update2, await bobGot.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // OthersInGroup: the pusher never gets its own update echoed back.
        var completed = await Task.WhenAny(aliceGotOwn.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(aliceGotOwn.Task, completed);

        // A third late joiner replays BOTH, in order.
        await using var carol = await ConnectAsync($"carol-{Guid.NewGuid()}");
        var carolJoin = await carol.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Equal(new[] { seed, update2 }, carolJoin!.UpdateLog);
    }

    // ---------------------------------------------------------------- refusal shapes

    [Fact]
    public async Task JoinEditSession_CanEditRefused_IsSilentNoOp_AndAuditedDeniedWithReason()
    {
        // Edit restricted to US; NZ can view (viewer half passes) but not edit.
        var (_, page) = await SeedPageAsync(editRestrictionNationality: "US");
        await using var nz = await ConnectAsync($"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        var result = await nz.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);

        // §6.7's shape: the same null a nonexistent page produces, nothing registered.
        Assert.Null(result);
        var registry = factory.Services.GetRequiredService<IEditSessionRegistry>();
        Assert.DoesNotContain(registry.GetAllMembers(), m => m.PageId == page.Id);

        // §7's half: the denial IS in the audit log, with the failing restriction,
        // on the realtime channel.
        var denied = Assert.Single(AuditRowsFor(page.Id, EditSessionAudit.JoinedAction));
        Assert.Equal(AuditOutcome.Denied, denied.Outcome);
        Assert.Equal(AuditChannel.Realtime, denied.Channel);
        Assert.Contains($"restriction:{page.Id}", denied.DetailsJson);

        // And a genuinely nonexistent page: same null, but NO audit row - §7's
        // outcome vocabulary has no access decision to record for an absent subject.
        var ghostPageId = Guid.NewGuid();
        Assert.Null(await nz.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", ghostPageId));
        Assert.Empty(AuditRowsFor(ghostPageId, EditSessionAudit.JoinedAction));
    }

    [Fact]
    public async Task JoinEditSession_HonoursSelectorGates_ThroughTheHubPrincipal()
    {
        // design.md §21.15 on the realtime channel: the hub builds its Principal through
        // the same PrincipalBuilder the HTTP path uses, so a selector claim on the token
        // admits (and its absence refuses) a co-editor exactly as it would a GraphQL
        // read. The page carries FRUIT/APPLE; the space's access grant confers APPLE to
        // everyone, so eligibility - the `fruit` claim - is the one gate that decides.
        Guid pageId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
            db.Users.Add(seeder);
            await db.SaveChangesAsync();

            var space = new Space
            {
                Key = $"ESS{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "Selector Edit Session Space",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = seeder.Id,
            };
            db.Spaces.Add(space);
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
                ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            });
            var access = new AccessRule
            {
                Kind = AccessRuleKind.AccessGrant, SpaceId = space.Id,
                ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
            };
            access.Selectors.Add(new AccessRuleSelector { Category = "FRUIT", Value = "APPLE" });
            db.AccessRules.Add(access);

            var page = new Page
            {
                SpaceId = space.Id, AncestorPath = "/", Slug = "apple", Title = "Apple Page",
                CurrentContent = "# Apple", CurrentRevisionNumber = 0,
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            };
            db.Pages.Add(page);
            await db.SaveChangesAsync();

            var marking = await db.PageMarkings.Include(m => m.Selectors).SingleAsync(m => m.PageId == page.Id);
            marking.Selectors.Add(new PageMarkingSelector { PageId = page.Id, Category = "FRUIT", Value = "APPLE" });
            await db.SaveChangesAsync();
            pageId = page.Id;
        }

        await using var eligible = await ConnectAsync($"eligible-{Guid.NewGuid()}", selectorClaims: [RocketWikiApiFactory.FruitClaim]);
        Assert.NotNull(await eligible.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", pageId));

        await using var ineligible = await ConnectAsync($"ineligible-{Guid.NewGuid()}");
        Assert.Null(await ineligible.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", pageId));

        // §7: the refusal names the selector gate by category, never by value.
        var denied = Assert.Single(AuditRowsFor(pageId, EditSessionAudit.JoinedAction), r => r.Outcome == AuditOutcome.Denied);
        Assert.Contains("selector:not_eligible:FRUIT", denied.DetailsJson);
        Assert.DoesNotContain("APPLE", denied.DetailsJson);
    }

    [Fact]
    public async Task JoinEditSession_OnReplicaSpace_Refused_AndAuditedWithReplicaReason()
    {
        // A replica (design.md §12): canEdit unconditionally false beneath every grant.
        var (_, page) = await SeedPageAsync(originInstanceId: "low-instance");
        await using var connection = await ConnectAsync($"replica-{Guid.NewGuid()}");

        var result = await connection.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);

        Assert.Null(result);
        var denied = Assert.Single(AuditRowsFor(page.Id, EditSessionAudit.JoinedAction));
        Assert.Equal(AuditOutcome.Denied, denied.Outcome);
        Assert.Contains("replica-read-only", denied.DetailsJson);
    }

    // ---------------------------------------------------------------- audit rows

    [Fact]
    public async Task JoinAndLeave_WriteSessionScopedAuditRows_OnTheRealtimeChannel()
    {
        var (space, page) = await SeedPageAsync();
        await using var connection = await ConnectAsync($"auditee-{Guid.NewGuid()}");

        Assert.NotNull(await connection.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));
        await connection.InvokeAsync("LeaveEditSession", page.Id);

        var joined = Assert.Single(AuditRowsFor(page.Id, EditSessionAudit.JoinedAction));
        Assert.Equal(AuditOutcome.Success, joined.Outcome);
        Assert.Equal(AuditChannel.Realtime, joined.Channel);
        Assert.Equal(AuditSubjectType.Page, joined.SubjectType);
        Assert.Equal(space.Key, joined.SpaceKey);
        Assert.Contains("seeder", joined.DetailsJson);
        Assert.NotNull(joined.UserId); // attributed, never a system-shaped row

        var left = Assert.Single(AuditRowsFor(page.Id, EditSessionAudit.LeftAction));
        Assert.Equal(AuditOutcome.Success, left.Outcome);
        Assert.Equal(AuditChannel.Realtime, left.Channel);
        Assert.Contains("\"left\"", left.DetailsJson);
        Assert.Equal(joined.UserId, left.UserId);
    }

    [Fact]
    public async Task Disconnect_WritesLeftRow_WithDisconnectedReason()
    {
        var (_, page) = await SeedPageAsync();
        var connection = await ConnectAsync($"dropper-{Guid.NewGuid()}");
        Assert.NotNull(await connection.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));

        await connection.DisposeAsync();

        // OnDisconnectedAsync runs asynchronously after the transport drops; poll
        // briefly rather than assuming synchronous completion.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<AuditEvent> leftRows;
        do
        {
            leftRows = AuditRowsFor(page.Id, EditSessionAudit.LeftAction);
        }
        while (leftRows.Count == 0 && DateTime.UtcNow < deadline && await Delay());

        var left = Assert.Single(leftRows);
        Assert.Contains("disconnected", left.DetailsJson);

        static async Task<bool> Delay()
        {
            await Task.Delay(50);
            return true;
        }
    }

    // ---------------------------------------------------------------- eviction

    [Fact]
    public async Task RuleChange_EvictsMemberWhoLostCanEdit_ClosesRelayAndAuditsDeparture()
    {
        var (_, page) = await SeedPageAsync();
        await using var us = await ConnectAsync($"us-{Guid.NewGuid()}", nationality: ["US"]);
        await using var nz = await ConnectAsync($"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        Assert.NotNull(await us.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));
        Assert.NotNull(await nz.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));

        var nzEvicted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        nz.On<Guid>("EvictedFromEditSession", pid => nzEvicted.TrySetResult(pid));
        var nzGotUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nz.On<Guid, byte[]>("UpdateReceived", (_, _) => nzGotUpdate.TrySetResult(true));

        // Restrict EDIT to US nationals via the real mutation, exercising the actual
        // resolver -> IPresenceRuleChangeNotifier wiring (same as the presence test).
        var adminClient = factory.CreateClient();
        adminClient.SetTestUser(sub: $"admin-{Guid.NewGuid()}", roles: ["admin"]);
        var mutationResult = await adminClient.PostGraphQLAsync($$"""
            mutation {
              createAccessRule(input: {
                kind: PAGE_RESTRICTION, spaceId: null, pageId: "{{page.Id}}", role: null, action: EDIT,
                expressionJson: {{JsonSerializer.Serialize(RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])))}}
              }) { rule { id } error { kind } }
            }
            """);
        Assert.Equal(JsonValueKind.Null,
            mutationResult.RootElement.GetProperty("data").GetProperty("createAccessRule").GetProperty("error").ValueKind);

        Assert.Equal(page.Id, await nzEvicted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Membership: the US editor stays, the NZ editor is gone.
        var registry = factory.Services.GetRequiredService<IEditSessionRegistry>();
        var member = Assert.Single(registry.GetAllMembers(), m => m.PageId == page.Id);
        Assert.Equal(us.ConnectionId, member.Member.ConnectionId);

        // The relay is closed, not just the roster: a post-eviction update from the
        // surviving editor must never reach the evicted one.
        await us.InvokeAsync("PushUpdate", page.Id, Encoding.UTF8.GetBytes("after-eviction"));
        var completed = await Task.WhenAny(nzGotUpdate.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(nzGotUpdate.Task, completed);

        // §7: the departure is recorded (reason evicted); the join row already existed.
        var left = Assert.Single(AuditRowsFor(page.Id, EditSessionAudit.LeftAction));
        Assert.Contains("evicted", left.DetailsJson);
    }

    // ---------------------------------------------------------------- seeder loss

    [Fact]
    public async Task SeederDisconnectsBeforeSeeding_RemainingMemberIsToldToReseed()
    {
        var (_, page) = await SeedPageAsync();
        var alice = await ConnectAsync($"alice-{Guid.NewGuid()}");
        await using var bob = await ConnectAsync($"bob-{Guid.NewGuid()}");

        var aliceJoin = await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Equal("seeder", aliceJoin!.Role);
        var bobJoin = await bob.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Equal("joiner", bobJoin!.Role);

        var bobReseed = new TaskCompletionSource<(Guid PageId, int BaseRevision, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bob.On<Guid, int, string>("ReseedRequired", (pid, baseRev, reason) => bobReseed.TrySetResult((pid, baseRev, reason)));

        // Alice vanishes without ever pushing the seed - Bob is promoted and told to
        // seed from CurrentContent at the session's base revision.
        await alice.DisposeAsync();

        var demand = await bobReseed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(page.Id, demand.PageId);
        Assert.Equal(0, demand.BaseRevision);
        Assert.Equal("seeder_lost", demand.Reason);
    }

    // ---------------------------------------------------------------- awareness

    [Fact]
    public async Task Awareness_IsRelayedToOthers_ButNeverLoggedForLateJoiners()
    {
        var (_, page) = await SeedPageAsync();
        await using var alice = await ConnectAsync($"alice-{Guid.NewGuid()}");
        await using var bob = await ConnectAsync($"bob-{Guid.NewGuid()}");
        await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        await bob.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);

        var bobAwareness = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.On<Guid, byte[]>("AwarenessReceived", (_, payload) => bobAwareness.TrySetResult(payload));

        var update = Encoding.UTF8.GetBytes("real-update");
        var caret = Encoding.UTF8.GetBytes("caret-position");
        await alice.InvokeAsync("PushUpdate", page.Id, update);
        await alice.InvokeAsync("PushAwareness", page.Id, caret);

        Assert.Equal(caret, await bobAwareness.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Ephemeral: a late joiner's replay contains the update and no awareness.
        await using var carol = await ConnectAsync($"carol-{Guid.NewGuid()}");
        var carolJoin = await carol.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Equal(new[] { update }, carolJoin!.UpdateLog);
    }
}
