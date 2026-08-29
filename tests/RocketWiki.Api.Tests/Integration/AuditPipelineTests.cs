using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §7's central guarantee, exercised through the API's own DI wiring
/// (not RocketWiki.Data.Tests' isolated SqliteTestBase) — proving the guarantee
/// still holds through this project's specific composition, not just in Core/Data
/// in isolation.
/// </summary>
public sealed class AuditPipelineTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task Mutation_WithoutAuditContext_ThrowsAndPersistsNothing()
    {
        var space = new Space { Key = $"AUD{Guid.NewGuid():N}"[..8], Name = "Audit Test Space", CreatedAtUtc = DateTime.UtcNow };
        var page = new Page { SpaceId = space.Id, Title = "Test Page", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            // AuditContext deliberately left null - simulating a code path that raises a
            // domain event without ever setting one.
            db.Spaces.Add(space);
            db.Pages.Add(page);
            db.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, Guid.NewGuid(), page.Title));

            await Assert.ThrowsAsync<MissingAuditContextException>(() => db.SaveChangesAsync());
        }

        // Fail-closed before any SQL is issued: neither the page nor the space it
        // depends on were sent to the database at all (design.md §7).
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await verifyDb.Spaces.AnyAsync(s => s.Id == space.Id));
        Assert.False(await verifyDb.Pages.AnyAsync(p => p.Id == page.Id));
        Assert.False(await verifyDb.AuditEvents.AnyAsync(e => e.SubjectId == page.Id));
    }

    [Fact]
    public async Task ReadAudit_WithNoResolvableActingUser_FailsClosed_NoRowWritten()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var actingUser = new FakeActingUserAccessor { ActingUserId = null };
        var auditContextAccessor = new FakeAuditContextAccessor
        {
            Current = new AuditContext(AuditChannel.GraphQl, "req-anon", "127.0.0.1"),
        };
        var sink = new DbAuditSink(db, actingUser, auditContextAccessor);
        var subjectId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.RecordAsync(new AuditRecord("page.view", AuditOutcome.Success, AuditSubjectType.Page, subjectId), CancellationToken.None));

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await verifyDb.AuditEvents.AnyAsync(e => e.SubjectId == subjectId));
    }

    [Fact]
    public async Task ReadAudit_WithoutAuditContext_ThrowsAndWritesNothing()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var actingUser = new FakeActingUserAccessor { ActingUserId = Guid.NewGuid() };
        var auditContextAccessor = new FakeAuditContextAccessor { Current = null };
        var sink = new DbAuditSink(db, actingUser, auditContextAccessor);

        var subjectId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.RecordAsync(new AuditRecord("page.view", AuditOutcome.Success, AuditSubjectType.Page, subjectId), CancellationToken.None));

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.False(await verifyDb.AuditEvents.AnyAsync(e => e.SubjectId == subjectId));
    }

    [Fact]
    public async Task ReadAudit_WithResolvedIdentityAndContext_WritesExactlyOneEvent_WithGivenChannel()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        // AuditEvent.UserId has a real FK to User (SQLite enforces it here just like SQL
        // Server would) - a random Guid isn't enough, an actual row must exist.
        var actingUser = new FakeActingUserAccessor { ActingUserId = await CreateUserAsync(db) };
        var auditContextAccessor = new FakeAuditContextAccessor
        {
            Current = new AuditContext(AuditChannel.Mcp, "req-mcp-1", "10.0.0.5", McpClient: "test-agent"),
        };
        var sink = new DbAuditSink(db, actingUser, auditContextAccessor);
        var pageId = Guid.NewGuid();

        await sink.RecordAsync(new AuditRecord("page.view", AuditOutcome.Success, AuditSubjectType.Page, pageId, SpaceKey: "ENG"), CancellationToken.None);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var events = await verifyDb.AuditEvents.Where(e => e.SubjectId == pageId).ToListAsync();

        var auditEvent = Assert.Single(events);
        Assert.Equal("page.view", auditEvent.Action);
        Assert.Equal(AuditChannel.Mcp, auditEvent.Channel);
        Assert.Equal("req-mcp-1", auditEvent.RequestId);
        Assert.Equal("10.0.0.5", auditEvent.ClientIp);
        Assert.Equal("test-agent", auditEvent.McpClient);
        Assert.Equal(AuditOutcome.Success, auditEvent.Outcome);
        Assert.Equal("ENG", auditEvent.SpaceKey);
        Assert.Equal(actingUser.ActingUserId, auditEvent.UserId);
    }

    [Fact]
    public async Task DeniedRead_IsRecordedWithDeniedOutcomeAndDetails()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var actingUser = new FakeActingUserAccessor { ActingUserId = await CreateUserAsync(db) };
        var auditContextAccessor = new FakeAuditContextAccessor
        {
            Current = new AuditContext(AuditChannel.GraphQl, "req-denied-1", "127.0.0.1"),
        };
        var sink = new DbAuditSink(db, actingUser, auditContextAccessor);
        var pageId = Guid.NewGuid();

        await sink.RecordAsync(
            new AuditRecord(
                "page.view",
                AuditOutcome.Denied,
                AuditSubjectType.Page,
                pageId,
                DetailsJson: """{"failingRestriction":"nationality in [NZ,US]"}"""),
            CancellationToken.None);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var auditEvent = await verifyDb.AuditEvents.SingleAsync(e => e.SubjectId == pageId);

        Assert.Equal(AuditOutcome.Denied, auditEvent.Outcome);
        Assert.Contains("failingRestriction", auditEvent.DetailsJson);
    }

    /// <summary>
    /// The anonymous convention (an empty list, the same absent shape every other read
    /// root gives) meeting design.md §7's refusal to write a UserId-less audit row. Both
    /// halves were right on their own and the seam between them was not: the empty list
    /// is non-null, so AuditFieldMiddleware dispatched a Success row for a request with
    /// no acting user, and DbAuditSink turned every one of these queries into an
    /// execution error. Every audited root that answers an anonymous caller with a
    /// non-null shape is listed here — the earlier tier missed this because no test ever
    /// aimed an anonymous client at an audited *list*.
    /// </summary>
    [Theory]
    [InlineData("{ spaces { key } }", "spaces")]
    [InlineData("{ pageTree(spaceId: \"00000000-0000-0000-0000-000000000000\") { id } }", "pageTree")]
    [InlineData("{ archivedSpaces { key } }", "archivedSpaces")]
    [InlineData("{ labels }", "labels")]
    [InlineData("{ labelDetails { id name } }", "labelDetails")]
    [InlineData("{ notifications { id } }", "notifications")]
    [InlineData("{ groups }", "groups")]
    [InlineData("{ attributeRegistry { key } }", "attributeRegistry")]
    [InlineData("{ search(query: \"anything\") { totalCount edges { cursor } } }", "search.edges")]
    public async Task AnonymousAuditedRead_AnswersEmpty_WithoutErrorOrAuditRow(string query, string emptyArrayPath)
    {
        var client = factory.CreateClient(); // no SetTestUser
        var before = await CountAuditEventsAsync();

        using var result = await client.PostGraphQLAsync(query);

        Assert.False(result.RootElement.TryGetProperty("errors", out var errors), errors.ToString());
        Assert.Equal(0, Walk(result.RootElement.GetProperty("data"), emptyArrayPath).GetArrayLength());
        Assert.Equal(before, await CountAuditEventsAsync());
    }

    /// <summary>
    /// The same seam on the other emission path: these two roots record their denial
    /// themselves rather than through AuditFieldMiddleware, so they reached DbAuditSink
    /// before anything had established there was a caller to attribute the row to. An
    /// anonymous caller is not an instance admin, so the honest answer is each field's
    /// own refusal - not the sink's "no resolvable acting user" leaking out as an
    /// execution error.
    /// </summary>
    [Theory]
    [InlineData("{ auditEvents(filter: {}) { totalCount } }", "Instance admin required to view the audit log.")]
    [InlineData("{ syncStatus { localInstanceId } }", "Instance admin required to view sync status.")]
    public async Task AnonymousAdminGatedRead_IsRefusedByItsOwnGate_WithoutAuditRow(string query, string expectedMessage)
    {
        var client = factory.CreateClient(); // no SetTestUser
        var before = await CountAuditEventsAsync();

        using var result = await client.PostGraphQLAsync(query);

        var errors = result.RootElement.GetProperty("errors");
        Assert.Equal(expectedMessage, errors[0].GetProperty("message").GetString());
        Assert.Equal(before, await CountAuditEventsAsync());
    }

    /// <summary>
    /// The half that must NOT be softened: a request that authenticated but carries no
    /// subject claim resolves no acting user, and design.md §7's "no anonymous wikis"
    /// makes that a bug to surface, not an anonymous read to wave through. The claims
    /// header is built by hand because SetTestUser always emits a `sub` - the whole
    /// point here is a token that authenticates without one.
    /// </summary>
    [Fact]
    public async Task AuthenticatedReadWithNoResolvableActingUser_StillFailsLoudly()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClaimsHeaderName, EncodeClaimsWithoutSubject());
        var before = await CountAuditEventsAsync();

        using var result = await client.PostGraphQLAsync("{ spaces { key } }");

        var errors = result.RootElement.GetProperty("errors");
        Assert.Contains("no resolvable acting user", errors[0].ToString(), StringComparison.Ordinal);
        Assert.Equal(before, await CountAuditEventsAsync());
    }

    private static string EncodeClaimsWithoutSubject()
    {
        var json = JsonSerializer.Serialize(new[] { new TestAuthHandler.TestClaim("name", "No Subject") });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Follows a dot-separated path through the GraphQL `data` object, so one
    /// theory can point at both a root list and a connection's `edges`.</summary>
    private static JsonElement Walk(JsonElement element, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            element = element.GetProperty(segment);
        }

        return element;
    }

    private async Task<int> CountAuditEventsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents.CountAsync();
    }

    private static async Task<Guid> CreateUserAsync(RocketWikiDbContext db)
    {
        var user = new User
        {
            Subject = $"audit-test-{Guid.NewGuid()}",
            DisplayName = "Audit Test User",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Plain in-memory stand-in for IActingUserAccessor - these tests construct
    /// DbAuditSink directly rather than resolving it through DI, since they use a
    /// bare `factory.Services.CreateScope()` with no real HttpContext behind it
    /// (the real ActingUserAccessor needs one; see its own doc for why).
    /// </summary>
    private sealed class FakeActingUserAccessor : IActingUserAccessor
    {
        public Guid? ActingUserId { get; set; }
    }

    /// <summary>Same rationale as FakeActingUserAccessor - the real
    /// CurrentAuditContextAccessor needs a real HttpContext.</summary>
    private sealed class FakeAuditContextAccessor : ICurrentAuditContextAccessor
    {
        public AuditContext? Current { get; set; }
    }
}
