using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §7: "an edit cannot exist without its audit record" - these prove that
/// guarantee at the database level, not just by reading RocketWikiDbContext's source.
/// RaiseDomainEvent + SaveChanges must commit the mutation and its AuditEvent row
/// together, and any failure in producing the audit row must leave neither persisted.
/// Two distinct failure modes are covered: mapping failures that never reach the
/// database at all (missing AuditContext, an unmapped event type), and a genuine
/// database-level rejection of the audit row itself (a foreign key violation), which is
/// the one that actually exercises relational transaction rollback rather than a
/// C#-side pre-flight check.
/// </summary>
public class DomainEventPipelineTests : SqliteTestBase
{
    [Fact]
    public void RaisingDomainEvent_WritesMutationAndAuditRow_InTheSameSaveChangesCall()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();

        using (var context = CreateContext())
        {
            context.AuditContext = new AuditContext(AuditChannel.GraphQl, "req-1", "127.0.0.1");
            context.Users.Add(actor);
            context.Spaces.Add(space);

            var page = TestData.NewPage(space, "getting-started");
            context.Pages.Add(page);
            context.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, actor.Id, page.Title));

            context.SaveChanges();
        }

        using var readContext = CreateContext();
        var page2 = readContext.Pages.Single(p => p.SpaceId == space.Id);
        var auditEvent = readContext.AuditEvents.Single(e => e.SubjectId == page2.Id);

        Assert.Equal("page.create", auditEvent.Action);
        Assert.Equal(AuditSubjectType.Page, auditEvent.SubjectType);
        Assert.Equal(actor.Id, auditEvent.UserId);
        Assert.Equal(AuditOutcome.Success, auditEvent.Outcome);
        Assert.Equal(space.Key, auditEvent.SpaceKey);
    }

    [Fact]
    public void MultipleDomainEvents_InOneUnitOfWork_EachProduceTheirOwnAuditRow()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using (var context = CreateContext())
        {
            context.AuditContext = new AuditContext(AuditChannel.GraphQl, "req-1", "127.0.0.1");
            context.Users.Add(user);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, user.Id, page.Title));

            var comment = new Comment
            {
                PageId = page.Id,
                Body = "Nice page!",
                AuthorUserId = user.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            context.Comments.Add(comment);
            context.RaiseDomainEvent(new CommentAddedEvent(comment.Id, page.Id, space.Id, space.Key, user.Id));

            context.SaveChanges();
        }

        using var readContext = CreateContext();
        Assert.Equal(2, readContext.AuditEvents.Count());
        Assert.Contains(readContext.AuditEvents, e => e.Action == "page.create");
        Assert.Contains(readContext.AuditEvents, e => e.Action == "comment.add");
    }

    [Fact]
    public void NoDomainEventRaised_SaveChangesBehavesNormally_NoAuditRowWritten()
    {
        // The pipeline must not force an audit row into existence for changes nobody
        // routed through RaiseDomainEvent - e.g. internal bookkeeping updates that
        // aren't user-facing mutations.
        var space = TestData.NewSpace();

        using (var context = CreateContext())
        {
            context.Spaces.Add(space);
            context.SaveChanges(); // no AuditContext set, no domain event raised - must not throw
        }

        using var readContext = CreateContext();
        Assert.Empty(readContext.AuditEvents);
        Assert.True(readContext.Spaces.Any(s => s.Id == space.Id));
    }

    [Fact]
    public void RaisingDomainEvent_WithoutAuditContextSet_ThrowsBeforeAnySqlIsSent_MutationNotPersisted()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using (var context = CreateContext())
        {
            // AuditContext deliberately left null.
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, Guid.NewGuid(), page.Title));

            Assert.Throws<MissingAuditContextException>(() => context.SaveChanges());
        }

        // Fail-closed before any SQL is issued: neither the page nor the space it
        // depends on were sent to the database at all.
        using var verifyContext = CreateContext();
        Assert.False(verifyContext.Spaces.Any(s => s.Id == space.Id));
        Assert.False(verifyContext.Pages.Any(p => p.Id == page.Id));
        Assert.Empty(verifyContext.AuditEvents);
    }

    [Fact]
    public void AuditRowRejectedByTheDatabase_RollsBackTheWholeTransaction_MutationNotPersistedEither()
    {
        // A real, database-level rejection of specifically the audit row, distinct from
        // the C#-side pre-flight failures above: ActorUserId doesn't correspond to any
        // existing User, so AuditEvent's own FK to User is violated (PRAGMA foreign_keys
        // is on - see SqliteTestBase). The Space insert below has nothing to do with
        // that FK and would succeed entirely on its own; this proves it doesn't survive
        // when the audit row sharing its SaveChanges call is rejected by SQLite itself -
        // the actual "one transaction" guarantee, not just "we validated first."
        var space = TestData.NewSpace();
        var nonExistentUserId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            context.AuditContext = new AuditContext(AuditChannel.GraphQl, "req-1", "127.0.0.1");
            context.Spaces.Add(space);
            context.RaiseDomainEvent(new PageDeletedEvent(Guid.NewGuid(), space.Id, space.Key, nonExistentUserId));

            Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
        }

        using var verifyContext = CreateContext();
        Assert.False(verifyContext.Spaces.Any(s => s.Id == space.Id));
        Assert.Empty(verifyContext.AuditEvents);
    }

    [Fact]
    public void UnmappedDomainEventType_ThrowsBeforeAnySqlIsSent_MutationNotPersisted()
    {
        // Belt-and-braces: DomainEventAuditMapper throws NotSupportedException for any
        // event type it has no case for (see its default arm) - a new mutation type
        // must not silently skip auditing just because nobody wired up its mapping yet.
        var space = TestData.NewSpace();

        using (var context = CreateContext())
        {
            context.AuditContext = new AuditContext(AuditChannel.GraphQl, "req-1", "127.0.0.1");
            context.Spaces.Add(space);
            context.RaiseDomainEvent(new UnmappedTestEvent(Guid.NewGuid()));

            Assert.Throws<NotSupportedException>(() => context.SaveChanges());
        }

        using var verifyContext = CreateContext();
        Assert.False(verifyContext.Spaces.Any(s => s.Id == space.Id));
    }

    private sealed record UnmappedTestEvent(Guid? ActorUserId) : IDomainEvent;
}
