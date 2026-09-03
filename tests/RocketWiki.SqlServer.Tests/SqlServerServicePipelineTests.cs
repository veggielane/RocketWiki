using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using RocketWiki.Data.Tests;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// Re-runs the most SQL-dialect-sensitive service-layer behaviors - each already
/// proven on SQLite in RocketWiki.Data.Tests - against real SQL Server, contributing
/// to the README/design.md §16 unverified item that no RocketWiki write path had ever
/// touched a real SQL Server ("no migration applied to real SQL Server", "everything
/// ... verified by tests, not by running"). Five scenarios, chosen for where the
/// providers genuinely diverge, not for breadth (breadth is the SQLite tier's job):
///
///  1. Subtree delete + restore-by-batch: AncestorPath prefix matching (StartsWith →
///     the provider's LIKE with escaping) + the IX_Pages_DeleteBatchId filtered index
///     + soft-delete query filters (PageServiceTests' subtree scenarios).
///  2. Move: the same prefix machinery rewriting every descendant path in one
///     transaction (PageServiceTests' move scenarios).
///  3. Outbox sequencing: design.md §12's gap-free per-space sequence - deliberately
///     NOT an identity column - under two racing units of work; the unique
///     (SpaceId, SequenceNumber) index must reject the loser and a retry must fill
///     the sequence with no gap (SyncOutboxTests' sequence scenarios; the unique
///     index enforcement itself is exactly the part SQLite proved differently).
///  4. Audit pipeline: mutation + audit row commit in ONE SQL Server transaction, and
///     a database-level rejection of the audit row rolls the mutation back too
///     (DomainEventPipelineTests); also pins the native bigint IDENTITY inside
///     AuditEvents' composite PK, which SQLite cannot represent at all
///     (SqliteAuditEventIdGenerator exists because of that).
///  5. Declared-schema enforcement: the CHECK constraint and nvarchar lengths that
///     SQLite's dynamic typing silently ignores (SqliteTestBase's own doc lists this
///     as a known coverage hole) must actually reject bad rows here.
/// </summary>
public sealed class SqlServerServicePipelineTests : SqlServerTestBase
{
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-sql", "127.0.0.1");

    public SqlServerServicePipelineTests(SqlServerContainerFixture fixture)
        : base(fixture)
    {
    }

    private static Principal EditorPrincipal() => Principal.Create("editor-sub", ["engineering"]);

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.RoleGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    [SqlServerFact]
    public async Task SubtreeDelete_ThenRestoreByBatch_RoundTripsTheWholeSubtree()
    {
        using var context = CreateContext();
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);
        var grandchild = TestData.NewPage(space, "grandchild", child);
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child, grandchild);
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, DefaultLocalInstanceId);

        var deleteResult = await service.DeletePageAsync(
            new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(deleteResult.IsSuccess);
        Assert.Equal(3, deleteResult.Value.DeletedPageCount);

        using (var verify = CreateContext())
        {
            // Soft-delete query filter: the live view of the space is empty...
            Assert.Equal(0, await verify.Pages.CountAsync(p => p.SpaceId == space.Id));
            // ...and the trashed rows all share one DeleteBatchId (the filtered-index column).
            var trashed = await verify.Pages.IgnoreQueryFilters()
                .Where(p => p.SpaceId == space.Id && p.IsDeleted)
                .ToListAsync();
            Assert.Equal(3, trashed.Count);
            Assert.Single(trashed.Select(p => p.DeleteBatchId).Distinct());
        }

        var restoreResult = await service.RestorePageAsync(
            new RestorePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(restoreResult.IsSuccess);
        Assert.Equal(3, restoreResult.Value.RestoredPageCount);

        using (var verify = CreateContext())
        {
            Assert.Equal(3, await verify.Pages.CountAsync(p => p.SpaceId == space.Id));
        }
    }

    [SqlServerFact]
    public async Task MovePage_RewritesEveryDescendantAncestorPath_InOneTransaction()
    {
        using var context = CreateContext();
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var oldParent = TestData.NewPage(space, "old-parent");
        var moved = TestData.NewPage(space, "moved", oldParent);
        var descendant = TestData.NewPage(space, "descendant", moved);
        var newParent = TestData.NewPage(space, "new-parent");
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(oldParent, moved, descendant, newParent);
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, DefaultLocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(moved.Id, newParent.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        using var verify = CreateContext();
        var movedAfter = await verify.Pages.SingleAsync(p => p.Id == moved.Id);
        var descendantAfter = await verify.Pages.SingleAsync(p => p.Id == descendant.Id);
        Assert.Equal(newParent.Id, movedAfter.ParentPageId);
        Assert.Equal($"/{newParent.Id}/", movedAfter.AncestorPath);
        Assert.Equal($"/{newParent.Id}/{moved.Id}/", descendantAfter.AncestorPath);
    }

    [SqlServerFact]
    public async Task OutboxSequence_TwoRacingWriters_UniqueIndexRejectsTheLoser_RetryLeavesNoGap()
    {
        Guid spaceId;
        Guid actorId;
        using (var seed = CreateContext())
        {
            var actor = TestData.NewUser();
            var space = TestData.NewSpace();
            space.IsExported = true; // outbox only journals exported spaces (design.md §12)
            spaceId = space.Id;
            actorId = actor.Id;
            seed.Users.Add(actor);
            seed.Spaces.Add(space);
            seed.SaveChanges();
        }

        // Two units of work, both observing LastOutboxSequence = 0 before either
        // commits - the textbook lost-update interleaving. On SQLite this could only
        // be simulated; here both writers hold real SQL Server connections.
        using var contextA = CreateContext();
        using var contextB = CreateContext();
        var spaceInA = await contextA.Spaces.SingleAsync(s => s.Id == spaceId);
        var spaceInB = await contextB.Spaces.SingleAsync(s => s.Id == spaceId);

        AddPageWithDomainEvent(contextA, spaceInA, actorId, "page-a");
        AddPageWithDomainEvent(contextB, spaceInB, actorId, "page-b");

        await contextA.SaveChangesAsync(); // wins: sequence 1

        // B computed sequence 1 too; the unique (SpaceId, SequenceNumber) index must
        // reject it - and with it B's page, in the same rolled-back transaction.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => contextB.SaveChangesAsync());

        // The loser retries as a fresh unit of work (what a request retry would do)
        // and must take sequence 2 - no gap, no duplicate, because the sequence comes
        // from the re-read row, never from an identity that burned a value on rollback.
        using (var retry = CreateContext())
        {
            var spaceForRetry = await retry.Spaces.SingleAsync(s => s.Id == spaceId);
            AddPageWithDomainEvent(retry, spaceForRetry, actorId, "page-b-retry");
            await retry.SaveChangesAsync();
        }

        using var verify = CreateContext();
        var sequences = await verify.SyncOutboxEvents
            .Where(e => e.SpaceId == spaceId)
            .OrderBy(e => e.SequenceNumber)
            .Select(e => e.SequenceNumber)
            .ToListAsync();
        Assert.Equal([1L, 2L], sequences);
        Assert.Equal(2, (await verify.Spaces.SingleAsync(s => s.Id == spaceId)).LastOutboxSequence);
        // B's rejected page must not exist - its whole transaction rolled back.
        Assert.False(await verify.Pages.AnyAsync(p => p.Slug == "page-b"));
    }

    [SqlServerFact]
    public async Task AuditPipeline_CommitsWithTheMutation_AndFkRejectionRollsBackBoth()
    {
        // Happy half: the mutation and its audit row are one transaction, and SQL
        // Server's native IDENTITY assigns the Id inside the composite (TimestampUtc,
        // Id) key - the mapping SQLite needs a value generator to fake.
        using var context = CreateContext();
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, DefaultLocalInstanceId);
        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "audited", "Audited", "# Audited"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        using (var verify = CreateContext())
        {
            var auditEvent = await verify.AuditEvents.SingleAsync(e => e.SubjectId == created.Value.Id);
            Assert.Equal("page.create", auditEvent.Action);
            Assert.Equal(actor.Id, auditEvent.UserId);
            Assert.True(auditEvent.Id > 0, "SQL Server IDENTITY must have assigned the audit row id");
        }

        // Failure half (DomainEventPipelineTests' AuditRowRejectedByTheDatabase..., on
        // the real provider): the audit row violates its FK to Users, and that must
        // take the otherwise-valid Space insert down with it - a genuine SQL Server
        // transaction rollback, not a C#-side pre-flight.
        var orphanSpace = TestData.NewSpace("OPS");
        using (var failing = CreateContext())
        {
            failing.AuditContext = AuditCtx;
            failing.Spaces.Add(orphanSpace);
            failing.RaiseDomainEvent(new PageDeletedEvent(
                Guid.NewGuid(), orphanSpace.Id, orphanSpace.Key, Guid.NewGuid() /* no such user */));

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => failing.SaveChangesAsync());
        }

        using (var verify = CreateContext())
        {
            Assert.False(await verify.Spaces.AnyAsync(s => s.Id == orphanSpace.Id));
            Assert.Equal(1, await verify.AuditEvents.CountAsync()); // only the happy half's row
        }
    }

    [SqlServerFact]
    public async Task DeclaredSchema_CheckConstraintAndColumnLengths_ActuallyReject()
    {
        using var context = CreateContext();
        var space = TestData.NewSpace();
        context.Spaces.Add(space);
        context.SaveChanges();

        // CK_AccessRules_KindColumnPairing: a SpaceGrant carrying a PageId is exactly
        // the malformed shape the constraint exists to make unstorable (design.md §6 -
        // fail closed at the schema too, not just in code).
        using (var violating = CreateContext())
        {
            violating.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.AccessGrant,
                SpaceId = space.Id,
                PageId = Guid.NewGuid(), // forbidden for a SpaceGrant
                ExpressionJson = """{ "everyone": true }""",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = Guid.NewGuid(),
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = Guid.NewGuid(),
            });

            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => violating.SaveChangesAsync());
            Assert.Contains("CK_AccessRules_KindColumnPairing", ex.InnerException?.Message);
        }

        // nvarchar(500) on Pages.Title: SQLite happily stores 501 chars (its base
        // class documents that hole); SQL Server must refuse.
        using (var tooLong = CreateContext())
        {
            var page = TestData.NewPage(space, "long-title");
            page.Title = new string('x', 501);
            tooLong.Pages.Add(page);

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => tooLong.SaveChangesAsync());
        }

        using var verify = CreateContext();
        Assert.Empty(verify.AccessRules.Where(r => r.PageId != null));
        Assert.Empty(verify.Pages.Where(p => p.SpaceId == space.Id));
    }

    private static void AddPageWithDomainEvent(RocketWiki.Data.RocketWikiDbContext context, Space trackedSpace, Guid actorId, string slug)
    {
        context.AuditContext = AuditCtx;
        var page = TestData.NewPage(trackedSpace, slug);
        context.Pages.Add(page);
        // A real seeded actor: the audit row this event produces carries a FK to
        // Users, and THIS test is about the outbox unique index, not that FK.
        context.RaiseDomainEvent(new PageCreatedEvent(page.Id, trackedSpace.Id, trackedSpace.Key, actorId, page.Title));
    }
}
