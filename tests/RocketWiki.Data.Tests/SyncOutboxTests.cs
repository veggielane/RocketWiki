using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using RocketWiki.Storage;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §12: the sync outbox is the second consumer of the domain-event pipeline -
/// append SyncOutboxEvent rows for mutations touching exported spaces, in the same
/// transaction as the change and its audit row, using a gap-free per-space sequence.
/// These run everything through the real services (PageService, AccessRuleService)
/// against a real SQLite database, the same way the audit-pipeline tests do.
/// </summary>
public class SyncOutboxTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("user-sub", groups);

    private static Space NewExportedSpace(string key = "ENG") => new()
    {
        Key = key,
        Name = $"{key} Space",
        OriginInstanceId = LocalInstanceId,
        IsExported = true,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule SpaceAdminGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.SpaceAdmin,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    // --- Page mutations produce the right event, with full payload ------------------

    [Fact]
    public async Task CreatePage_OnExportedSpace_ProducesPageUpsertEvent_WithFullContentAndSequenceOne()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home content"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        var outboxEvent = context.SyncOutboxEvents.Single();
        Assert.Equal(space.Id, outboxEvent.SpaceId);
        Assert.Equal(1, outboxEvent.SequenceNumber);
        Assert.Equal(SyncEventType.PageUpsert, outboxEvent.EventType);
        Assert.Contains("# Home content", outboxEvent.PayloadJson); // full Markdown, not a diff
        Assert.Contains(result.Value.Id.ToString(), outboxEvent.PayloadJson);

        Assert.Equal(1, context.Spaces.Single(s => s.Id == space.Id).LastOutboxSequence);
    }

    [Fact]
    public async Task UpdatePageContent_OnExportedSpace_ProducesSecondPageUpsertEvent_SequenceIncrements()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 1;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(TestData.NewRevision(page, actor, 1));
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(page.Id, 1, "Updated", "# Updated content", null), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        var outboxEvent = context.SyncOutboxEvents.Single();
        Assert.Equal(1, outboxEvent.SequenceNumber); // first sync-relevant mutation on this space
        Assert.Equal(SyncEventType.PageUpsert, outboxEvent.EventType);
        Assert.Contains("# Updated content", outboxEvent.PayloadJson);
    }

    [Fact]
    public async Task MovePage_OnExportedSpace_ProducesPageMoveEvent_WithNewAncestorPath()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var oldParent = TestData.NewPage(space, "old-parent");
        var newParent = TestData.NewPage(space, "new-parent");
        var page = TestData.NewPage(space, "page", oldParent);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(oldParent, newParent, page);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(new MovePageRequest(page.Id, newParent.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        var outboxEvent = context.SyncOutboxEvents.Single();
        Assert.Equal(SyncEventType.PageMove, outboxEvent.EventType);
        Assert.Contains(newParent.Id.ToString(), outboxEvent.PayloadJson);
    }

    [Fact]
    public async Task DeleteSubtree_OnExportedSpace_ProducesOnePageDeleteEvent_WithEveryPageId()
    {
        // Contrast with audit: audit only ever gets a count (design.md §6.4.1), but the
        // sync outbox needs concrete ids to actually replicate the deletion.
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        var outboxEvent = context.SyncOutboxEvents.Single();
        Assert.Equal(SyncEventType.PageDelete, outboxEvent.EventType);
        Assert.Contains(root.Id.ToString(), outboxEvent.PayloadJson);
        Assert.Contains(child.Id.ToString(), outboxEvent.PayloadJson);
    }

    [Fact]
    public async Task RestoreSubtree_OnExportedSpace_ProducesOnePageRestoreEvent()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        await service.DeletePageAsync(new DeletePageRequest(page.Id), EditorPrincipal(), actor.Id, AuditCtx);
        var result = await service.RestorePageAsync(new RestorePageRequest(page.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        var restoreEvent = context.SyncOutboxEvents.OrderBy(e => e.SequenceNumber).Last();
        Assert.Equal(SyncEventType.PageRestore, restoreEvent.EventType);
        Assert.Contains(page.Id.ToString(), restoreEvent.PayloadJson);
    }

    [Fact]
    public async Task AddComment_OnExportedSpace_ProducesCommentEvent_WithBody()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);
        var comment = new Comment
        {
            PageId = page.Id,
            Body = "This needs a diagram.",
            AuthorUserId = actor.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };

        using var context = CreateContext();
        context.AuditContext = AuditCtx;
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(comment);
        context.RaiseDomainEvent(new CommentAddedEvent(comment.Id, page.Id, space.Id, space.Key, actor.Id));
        context.SaveChanges();

        var outboxEvent = context.SyncOutboxEvents.Single();
        Assert.Equal(SyncEventType.Comment, outboxEvent.EventType);
        Assert.Contains("This needs a diagram.", outboxEvent.PayloadJson);
    }

    // --- Restrictions sync, space grants never do ------------------------------------

    [Fact]
    public async Task PageRestrictionChange_OnExportedSpace_ProducesRestrictionsEvent_WithFullBeforeAfter()
    {
        var admin = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.PageRestriction, null, page.Id, null, PageAction.View, """{ "group": "top-secret" }"""),
            EditorPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        var outboxEvent = context.SyncOutboxEvents.Single();
        Assert.Equal(SyncEventType.Restrictions, outboxEvent.EventType);
        Assert.Contains("top-secret", outboxEvent.PayloadJson);
        Assert.Contains("\"before\":null", outboxEvent.PayloadJson); // creation, per design.md §7
    }

    [Fact]
    public async Task SpaceGrantChange_OnExportedSpace_ProducesNoOutboxEvent()
    {
        // design.md §12: "space grants stay local to each instance" - even on an
        // exported space, a SpaceGrant change must never sync; only restrictions do.
        var admin = TestData.NewUser();
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.Viewer, null, """{ "everyone": true }"""),
            EditorPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        Assert.Empty(context.SyncOutboxEvents);
        Assert.Equal(0, context.Spaces.Single(s => s.Id == space.Id).LastOutboxSequence);
    }

    // --- The exported/non-exported/replica boundary, tested explicitly ---------------

    [Fact]
    public async Task NonExportedNativeSpace_ProducesNoOutboxEvent()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace(); // IsExported defaults to false

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(context.SyncOutboxEvents);
        Assert.Equal(0, context.Spaces.Single(s => s.Id == space.Id).LastOutboxSequence);
    }

    [Fact]
    public async Task ReplicaSpace_ProducesNoOutboxEvent_EvenForALegitimateLocallyManagedRuleChange()
    {
        // Page mutations can't be used to probe this boundary at all - PageService
        // refuses every write on a replica with ReadOnlyReplicaError before the outbox
        // writer would ever run. AccessRuleService is the one real path where a
        // domain event legitimately fires on a replica (rule management is locally
        // managed there - design.md §12), so it's the meaningful way to prove a
        // replica never produces a sync entry even for a change that's genuinely allowed.
        var admin = TestData.NewUser();
        var space = TestData.NewSpace(); // OriginInstanceId defaults to "local-instance" in TestData
        space.OriginInstanceId = "some-other-instance"; // replica relative to LocalInstanceId
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.PageRestriction, null, page.Id, null, PageAction.View, """{ "group": "top-secret" }"""),
            EditorPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess); // rule management is allowed on a replica
        Assert.Empty(context.SyncOutboxEvents); // but it must never be synced onward
    }

    [Fact]
    public async Task SpaceLifecycleEvents_NeverProduceOutboxEvents_EvenOnAnExportedSpace()
    {
        // No SyncEventType exists for space metadata (data-model.md); renaming an
        // exported space is a real, allowed mutation but has nothing to sync.
        var admin = TestData.NewUser();
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.SaveChanges();

        var spaceService = new SpaceService(context, LocalInstanceId);
        var result = await spaceService.RenameAsync(
            new RenameSpaceRequest(space.Id, "New Name", null), EditorPrincipal(), isInstanceAdmin: true, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(context.SyncOutboxEvents);
    }

    // --- The sequence property itself -------------------------------------------------

    [Fact]
    public async Task SequentialMutations_OnTheSameSpace_ProduceAContiguousGapFreeSequence()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);

        var page1 = await service.CreatePageAsync(new CreatePageRequest(space.Id, null, "page-1", "Page 1", "# 1"), EditorPrincipal(), actor.Id, AuditCtx);
        var page2 = await service.CreatePageAsync(new CreatePageRequest(space.Id, null, "page-2", "Page 2", "# 2"), EditorPrincipal(), actor.Id, AuditCtx);
        var page3 = await service.CreatePageAsync(new CreatePageRequest(space.Id, null, "page-3", "Page 3", "# 3"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(page1.IsSuccess && page2.IsSuccess && page3.IsSuccess);

        var sequenceNumbers = context.SyncOutboxEvents.OrderBy(e => e.SequenceNumber).Select(e => e.SequenceNumber).ToList();

        Assert.Equal(new long[] { 1, 2, 3 }, sequenceNumbers);
        Assert.Equal(3, context.Spaces.Single(s => s.Id == space.Id).LastOutboxSequence);
    }

    [Fact]
    public async Task RolledBackMutation_ConsumesNoSequenceNumber()
    {
        // The failed attempt below raises a sync-relevant domain event with an actor
        // that doesn't correspond to any User row, so the FK on PageRevision/AuditEvent
        // rejects the whole transaction (same technique as the audit rollback test) -
        // proving Space.LastOutboxSequence's in-memory increment never reaches the
        // database when the surrounding transaction fails.
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var nonExistentActor = Guid.NewGuid();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);

        // A nonexistent actor violates the FK on PageRevision.AuthorUserId (and
        // AuditEvent.UserId), so SaveChangesAsync itself throws - this is a DB-level
        // rejection, not a typed PageMutationResult failure.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "will-fail", "Will Fail", "# nope"), EditorPrincipal(), nonExistentActor, AuditCtx));

        using (var verifyContext = CreateContext())
        {
            Assert.Empty(verifyContext.SyncOutboxEvents);
            Assert.Equal(0, verifyContext.Spaces.Single(s => s.Id == space.Id).LastOutboxSequence);
        }

        // A subsequent, valid mutation must start the sequence at 1, not 2 - the
        // failed attempt burned nothing.
        using var retryContext = CreateContext();
        var retryService = new PageService(retryContext, LocalInstanceId);
        var succeeded = await retryService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "will-succeed", "Will Succeed", "# yes"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(succeeded.IsSuccess);
        Assert.Equal(1, retryContext.SyncOutboxEvents.Single().SequenceNumber);
    }

    // --- Comments, labels, attachments -------------------------------------------------

    [Fact]
    public async Task CommentEditAndDelete_OnExportedSpace_BothProduceCommentEvents()
    {
        var author = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var commentService = new CommentService(context, LocalInstanceId);
        var added = await commentService.AddCommentAsync(new AddCommentRequest(page.Id, null, "Original"), EditorPrincipal(), author.Id, AuditCtx);
        Assert.True(added.IsSuccess);

        await commentService.EditCommentAsync(new EditCommentRequest(added.Value.Id, "Edited"), EditorPrincipal(), author.Id, AuditCtx);
        await commentService.DeleteCommentAsync(new DeleteCommentRequest(added.Value.Id), EditorPrincipal(), author.Id, AuditCtx);

        var commentEvents = context.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.Comment).OrderBy(e => e.SequenceNumber).ToList();
        Assert.Equal(3, commentEvents.Count); // add, edit, delete - each its own outbox row
        Assert.Contains("Original", commentEvents[0].PayloadJson);
        Assert.Contains("Edited", commentEvents[1].PayloadJson);
        // Tombstone delete: the payload reflects the comment's blanked-body state at
        // the moment of delete, same as PageUpsert re-sending full current state.
        Assert.DoesNotContain("Edited", commentEvents[2].PayloadJson);
    }

    [Fact]
    public async Task LabelAttachAndDetach_OnExportedSpace_ProduceLabelsEvents_ButCreateDoesNot()
    {
        var admin = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var labelService = new LabelService(context, LocalInstanceId);
        var label = await labelService.CreateLabelAsync(new CreateLabelRequest(space.Id, "how-to"), EditorPrincipal(), admin.Id, AuditCtx);
        Assert.True(label.IsSuccess);

        // Creation alone produces nothing - no SyncEventType exists for a bare label
        // definition with nothing attached yet.
        Assert.Empty(context.SyncOutboxEvents);

        await labelService.AttachLabelAsync(new AttachLabelRequest(page.Id, label.Value.Id), EditorPrincipal(), admin.Id, AuditCtx);
        await labelService.DetachLabelAsync(new DetachLabelRequest(page.Id, label.Value.Id), EditorPrincipal(), admin.Id, AuditCtx);

        var labelEvents = context.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.Labels).OrderBy(e => e.SequenceNumber).ToList();
        Assert.Equal(2, labelEvents.Count);
        Assert.Contains("\"action\":\"attach\"", labelEvents[0].PayloadJson);
        Assert.Contains("\"action\":\"detach\"", labelEvents[1].PayloadJson);
        Assert.Contains("how-to", labelEvents[0].PayloadJson);
    }

    [Fact]
    public async Task AttachmentAddAndDelete_OnExportedSpace_ProduceAttachmentEvents_KeyedByContentHash()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);
        var storage = CreateFileStorage(out var tempDir);
        try
        {
            using var context = CreateContext();
            context.Users.Add(actor);
            context.Spaces.Add(space);
            context.Pages.Add(page);
            context.AccessRules.Add(EditorGrant(space.Id));
            context.SaveChanges();

            var attachmentService = new AttachmentService(context, storage, LocalInstanceId);
            var uploaded = await attachmentService.UploadAsync(
                new UploadAttachmentRequest(page.Id, "diagram.png", "image/png", new MemoryStream("bytes"u8.ToArray())),
                EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(uploaded.IsSuccess);

            await attachmentService.DeleteAsync(new DeleteAttachmentRequest(uploaded.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);

            var attachmentEvents = context.SyncOutboxEvents.Where(e => e.EventType == SyncEventType.Attachment).OrderBy(e => e.SequenceNumber).ToList();
            Assert.Equal(2, attachmentEvents.Count);
            Assert.Contains(Convert.ToHexString(uploaded.Value.ContentHash), attachmentEvents[0].PayloadJson);
            // The local storage key is opaque to this instance and must never appear -
            // only ContentHash is meaningful for the (not-yet-built) bundle blobs folder.
            Assert.DoesNotContain(uploaded.Value.StorageKey, attachmentEvents[0].PayloadJson);
            Assert.Contains("\"isDeleted\":true", attachmentEvents[1].PayloadJson);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static IFileStorage CreateFileStorage(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "rocketwiki-sync-outbox-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new FileStorageOptions { FileSystem = new FileSystemFileStorageOptions { Root = tempDir } });
        return new FileSystemFileStorage(options);
    }
}
