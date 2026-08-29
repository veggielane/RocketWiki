using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The two halves of co-editing that meet the durable write path (design.md §8/§7):
/// multi-author attribution on updatePageContent — including the adversarial cases
/// that pin "contributors come only from the server's session registry" — and the
/// log-cap save-and-reseed flow (exercised against a derived factory with tiny caps).
/// </summary>
public sealed class EditSessionContributorAndCapTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private static async Task<HubConnection> ConnectAsync(RocketWikiApiFactory root, TestServer server, string sub)
    {
        var claimsHeader = TestUserHttpClientExtensions.BuildEncodedClaimsHeaderValue(sub, name: sub);
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthHandler.ClaimsHeaderName, claimsHeader);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private async Task<(Space Space, Page Page)> SeedPageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"EC{Guid.NewGuid():N}"[..8],
            Name = "Contributor Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Contributor Page",
            CurrentContent = "# v0", CurrentRevisionNumber = 0,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return (space, page);
    }

    private Guid UserIdBySub(string sub)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return db.Users.AsNoTracking().Single(u => u.Subject == sub).Id;
    }

    [Fact]
    public async Task SessionSave_RecordsBothContributors_AuthorStaysWhoPressedSave()
    {
        var (_, page) = await SeedPageAsync();
        var aliceSub = $"alice-{Guid.NewGuid()}";
        var bobSub = $"bob-{Guid.NewGuid()}";
        await using var alice = await ConnectAsync(factory, factory.Server, aliceSub);
        await using var bob = await ConnectAsync(factory, factory.Server, bobSub);

        Assert.NotNull(await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));
        Assert.NotNull(await bob.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));
        await alice.InvokeAsync("PushUpdate", page.Id, Encoding.UTF8.GetBytes("alice-typed"));
        await bob.InvokeAsync("PushUpdate", page.Id, Encoding.UTF8.GetBytes("bob-typed"));

        // Bob presses save - the ordinary mutation, no contributors input exists.
        var bobClient = factory.CreateClient();
        bobClient.SetTestUser(sub: bobSub);
        var save = await bobClient.PostGraphQLAsync($$"""
            mutation {
              updatePageContent(input: {
                pageId: "{{page.Id}}", expectedRevisionNumber: 0,
                title: "Co-edited", content: "# co-edited v1", editSummary: null
              }) { page { id currentRevisionNumber } error { kind } }
            }
            """);
        Assert.Equal(JsonValueKind.Null,
            save.RootElement.GetProperty("data").GetProperty("updatePageContent").GetProperty("error").ValueKind);

        var aliceId = UserIdBySub(aliceSub);
        var bobId = UserIdBySub(bobSub);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var revision = db.PageRevisions.AsNoTracking().Single(r => r.PageId == page.Id && r.RevisionNumber == 1);

        // AuthorUserId = who pressed save; contributor rows = who typed (both, here).
        Assert.Equal(bobId, revision.AuthorUserId);
        var contributorIds = db.PageRevisionContributors.AsNoTracking()
            .Where(c => c.PageRevisionId == revision.Id)
            .Select(c => c.UserId)
            .ToList();
        Assert.Equal(new[] { aliceId, bobId }.OrderBy(g => g), contributorIds.OrderBy(g => g));

        // §7: the save's audit row names them too.
        var edit = db.AuditEvents.AsNoTracking()
            .Single(e => e.Action == "page.edit" && e.SubjectId == page.Id && e.Outcome == AuditOutcome.Success);
        Assert.Contains(aliceId.ToString(), edit.DetailsJson);
        Assert.Contains(bobId.ToString(), edit.DetailsJson);

        // GraphQL surfaces the attribution (batched loader) - and the author byline
        // remains separate.
        var history = await bobClient.PostGraphQLAsync($$"""
            query {
              page(id: "{{page.Id}}") {
                revisions { revisionNumber authorUserId author { id displayName } contributors { id } }
              }
            }
            """);
        var revisions = history.RootElement.GetProperty("data").GetProperty("page").GetProperty("revisions");
        var rev1 = revisions.EnumerateArray().Single(r => r.GetProperty("revisionNumber").GetInt32() == 1);
        var contributorsJson = rev1.GetProperty("contributors").EnumerateArray()
            .Select(c => Guid.Parse(c.GetProperty("id").GetString()!))
            .ToList();
        Assert.Equal(new[] { aliceId, bobId }.OrderBy(g => g), contributorsJson.OrderBy(g => g));

        // `author` resolves to the same person as the raw `authorUserId`, with a
        // name attached - the history screen lists people, not Guids, and it is
        // the only field there that is always populated (a solo save has no
        // contributor rows at all).
        var author = rev1.GetProperty("author");
        Assert.Equal(bobId, Guid.Parse(author.GetProperty("id").GetString()!));
        Assert.False(string.IsNullOrWhiteSpace(author.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task ClientSuppliedContributors_AreStructurallyImpossible_AndNonMemberSavesAttachNothing()
    {
        var (_, page) = await SeedPageAsync();
        var aliceSub = $"alice-{Guid.NewGuid()}";
        await using var alice = await ConnectAsync(factory, factory.Server, aliceSub);
        Assert.NotNull(await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));
        await alice.InvokeAsync("PushUpdate", page.Id, Encoding.UTF8.GetBytes("alice-typed"));

        // Forgery attempt 1: a contributors field on the input. It does not exist in
        // the schema (deliberately - see IPageService's doc), so validation refuses
        // the document outright.
        var eveClient = factory.CreateClient();
        eveClient.SetTestUser(sub: $"eve-{Guid.NewGuid()}");
        var forged = await eveClient.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                mutation {
                  updatePageContent(input: {
                    pageId: "{{page.Id}}", expectedRevisionNumber: 0,
                    title: "Forged", content: "# forged",
                    contributors: ["{{Guid.NewGuid()}}"]
                  }) { page { id } error { kind } }
                }
                """,
        });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        // Forgery attempt 2 (subtler): Eve holds canEdit and saves normally while the
        // session is live - but she is NOT a member, so the session's contributor set
        // must not decorate her save; attribution belongs to session content only.
        var save = await eveClient.PostGraphQLAsync($$"""
            mutation {
              updatePageContent(input: {
                pageId: "{{page.Id}}", expectedRevisionNumber: 0,
                title: "Outside the session", content: "# outside", editSummary: null
              }) { page { id } error { kind } }
            }
            """);
        Assert.Equal(JsonValueKind.Null,
            save.RootElement.GetProperty("data").GetProperty("updatePageContent").GetProperty("error").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var revision = db.PageRevisions.AsNoTracking().Single(r => r.PageId == page.Id && r.RevisionNumber == 1);
        Assert.Empty(db.PageRevisionContributors.AsNoTracking().Where(c => c.PageRevisionId == revision.Id).ToList());
    }

    [Fact]
    public async Task LogCap_TriggersReseedRequired_AndReseedReplacesTheLateJoinerLog()
    {
        // A derived factory with tiny caps - same SQLite file, same pipeline, small
        // CoEdit numbers so the cap is reachable with a handful of bytes.
        using var capped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CoEdit:LogCapBytes"] = "64",
                    ["CoEdit:UpdateMaxBytes"] = "48",
                })));

        var (_, page) = await SeedPageAsync();
        await using var alice = await ConnectAsync(factory, capped.Server, $"alice-{Guid.NewGuid()}");
        await using var bob = await ConnectAsync(factory, capped.Server, $"bob-{Guid.NewGuid()}");

        var aliceJoin = await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Equal("seeder", aliceJoin!.Role);
        Assert.NotNull(await bob.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id));

        var aliceReseed = new TaskCompletionSource<(Guid PageId, int BaseRevision, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<Guid, int, string>("ReseedRequired", (pid, baseRev, reason) => aliceReseed.TrySetResult((pid, baseRev, reason)));

        // Two 40-byte updates cross the 64-byte cap; the SEEDER is the designee.
        await bob.InvokeAsync("PushUpdate", page.Id, new byte[40]);
        await bob.InvokeAsync("PushUpdate", page.Id, new byte[40]);

        var demand = await aliceReseed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(page.Id, demand.PageId);
        Assert.Equal("log_cap", demand.Reason);

        // Alice performs the flow's second half: (save happens over GraphQL in real
        // life - irrelevant to the log swap itself) and hands back one full-state
        // snapshot; the log collapses to exactly that for every future joiner.
        var snapshot = Encoding.UTF8.GetBytes("full-state-snapshot");
        await alice.InvokeAsync("ReseedEditSession", page.Id, snapshot);

        await using var carol = await ConnectAsync(factory, capped.Server, $"carol-{Guid.NewGuid()}");
        var carolJoin = await carol.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Equal(new[] { snapshot }, carolJoin!.UpdateLog);

        // Size caps: an oversized update (49 > 48) is dropped - not relayed, not logged.
        var carolGotUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        carol.On<Guid, byte[]>("UpdateReceived", (_, _) => carolGotUpdate.TrySetResult(true));
        await bob.InvokeAsync("PushUpdate", page.Id, new byte[49]);
        var completed = await Task.WhenAny(carolGotUpdate.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(carolGotUpdate.Task, completed);

        var registry = capped.Services.GetRequiredService<IEditSessionRegistry>();
        Assert.Single(registry.GetLogSnapshot(page.Id)); // still just the snapshot
    }
}
