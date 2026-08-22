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
