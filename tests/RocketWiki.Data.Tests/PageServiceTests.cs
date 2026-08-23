using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §8: the shared page-mutation path. Covers the invariants the task called
/// out explicitly: canEdit enforcement via EffectivePermissionCalculator, replica
/// read-only, optimistic concurrency (StaleRevisionError), and - the one that "deserves
/// real tests" - AncestorPath rewriting the whole subtree on a move.
/// </summary>
public class PageServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("user-sub", groups);

    private static AccessRule ViewRestriction(Guid pageId, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = expressionJson,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    /// <summary>
    /// Goes through the real IAccessRuleService (§6.5.2's instance-admin arm), not a raw
    /// context.AccessRules.Add(...) - that direct-insert shortcut used to be how this
    /// file's setup silently routed around the bootstrap deadlock described in §6.5.1/
    /// §6.5.2: a freshly created space has zero grants, so the space-admin check that
    /// gates AccessRuleService.CreateAsync could never pass for anyone until the fix
    /// landed. Flushes first so the Space/Pages this test already `Add()`-ed are visible
    /// to the service's own query for the space.
    /// </summary>
    private static async Task GrantSpaceRoleAsync(RocketWikiDbContext context, Guid spaceId, SpaceRole role, Guid actingUserId)
    {
        await context.SaveChangesAsync();
        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, spaceId, null, role, null, """{ "everyone": true }"""),
            Principal.Create("test-bootstrap", []), isInstanceAdmin: true, actingUserId, AuditCtx);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Test setup grant failed: {result.Error}");
        }
    }

    // --- Create -----------------------------------------------------------------------

    [Fact]
    public async Task CreatePage_TopLevel_Succeeds_WithFirstRevision()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("/", result.Value.AncestorPath);
        Assert.Equal(1, result.Value.CurrentRevisionNumber);
        Assert.Single(context.PageRevisions);
        Assert.Contains(context.AuditEvents, e => e.Action == "page.create");
    }

    [Fact]
    public async Task CreatePage_Nested_GetsParentPathPlusParentId()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal($"/{parent.Id}/", result.Value.AncestorPath);
    }

    [Fact]
    public async Task CreatePage_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance"; // replica relative to LocalInstanceId

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    [Fact]
    public async Task CreatePage_WithoutEditorRole_ReturnsForbiddenError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Viewer, actor.Id); // viewer only - cannot edit

        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task CreatePage_DuplicateSlugAmongSiblings_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var existing = TestData.NewPage(space, "home");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(existing);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Another Home", "# Another"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    // --- Update -------------------------------------------------------------------------

    [Fact]
    public async Task UpdatePageContent_Succeeds_IncrementsRevision()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 1;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(TestData.NewRevision(page, actor, revisionNumber: 1));
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(page.Id, 1, "Updated title", "# Updated", "typo fix"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.CurrentRevisionNumber);
        Assert.Equal("Updated title", result.Value.Title);
        Assert.Equal(2, context.PageRevisions.Count(r => r.PageId == page.Id));

        // A solo (non-session) save: no contributor rows, and the page.edit audit
        // row keeps its original details shape - no "contributors" key at all
        // (design.md §8 co-editing: absent means "no session", not "empty session").
        Assert.Empty(context.PageRevisionContributors.ToList());
        var audit = context.AuditEvents.Single(e => e.Action == "page.edit");
        Assert.DoesNotContain("contributors", audit.DetailsJson);
    }

    /// <summary>
    /// design.md §8 co-editing / §7: a session save records its contributors as
    /// PageRevisionContributor rows in the SAME transaction as the revision, and the
    /// page.edit audit row names them. AuthorUserId stays "who pressed save".
    /// </summary>
    [Fact]
    public async Task UpdatePageContent_WithSessionContributors_WritesAttributionRowsAndAuditDetails()
    {
        var actor = TestData.NewUser();
        var coAuthor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 1;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Users.Add(coAuthor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(TestData.NewRevision(page, actor, revisionNumber: 1));
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(page.Id, 1, "Co-edited", "# co-edited", null),
            EditorPrincipal(), actor.Id, AuditCtx,
            sessionContributorUserIds: [actor.Id, coAuthor.Id, coAuthor.Id]); // dupes are defense-in-depth deduped

        Assert.True(result.IsSuccess);
        var revision = context.PageRevisions.Single(r => r.PageId == page.Id && r.RevisionNumber == 2);
        Assert.Equal(actor.Id, revision.AuthorUserId);

        var contributorIds = context.PageRevisionContributors
            .Where(c => c.PageRevisionId == revision.Id)
            .Select(c => c.UserId)
            .ToList();
        Assert.Equal(new[] { actor.Id, coAuthor.Id }.OrderBy(g => g), contributorIds.OrderBy(g => g));

        var audit = context.AuditEvents.Single(e => e.Action == "page.edit");
        Assert.Contains("contributors", audit.DetailsJson);
        Assert.Contains(coAuthor.Id.ToString(), audit.DetailsJson);
    }

    [Fact]
    public async Task UpdatePageContent_StaleRevision_ReturnsStaleRevisionErrorWithCurrentState()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 5;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(page.Id, 3, "Updated title", "# Updated", null), // stale: expected 3, actual 5
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<StaleRevisionError>(result.Error);
        Assert.Equal(3, error.ExpectedRevisionNumber);
        Assert.Equal(5, error.ActualRevisionNumber);
        Assert.Equal(page.Title, error.LatestTitle);
    }

    [Fact]
    public async Task UpdatePageContent_BlockedByViewRestriction_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 1;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.AccessRules.Add(ViewRestriction(page.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(page.Id, 1, "Updated title", "# Updated", null),
            EditorPrincipal(), actor.Id, AuditCtx); // principal not in "top-secret"

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    // --- Move: AncestorPath rewrite (the important one) ---------------------------------

    [Fact]
    public async Task MovePage_SingleLeaf_UpdatesOwnAncestorPathOnly()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var oldParent = TestData.NewPage(space, "old-parent");
        var newParent = TestData.NewPage(space, "new-parent");
        var leaf = TestData.NewPage(space, "leaf", oldParent);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(oldParent, newParent, leaf);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(leaf.Id, newParent.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal($"/{newParent.Id}/", result.Value.AncestorPath);
        Assert.Equal(newParent.Id, result.Value.ParentPageId);
    }

    [Fact]
    public async Task MovePage_ToTopLevel_AncestorPathBecomesRoot()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");
        var child = TestData.NewPage(space, "child", parent);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(parent, child);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(child.Id, null, 0), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("/", result.Value.AncestorPath);
        Assert.Null(result.Value.ParentPageId);
    }

    [Fact]
    public async Task MovePage_WithMultiLevelSubtree_RewritesEveryDescendantAncestorPath()
    {
        // Tree: Root -> A -> B -> C -> D  (A is moved under X)
        // After moving A under X, expect:
        //   A: /{X}/
        //   B: /{X}/{A}/
        //   C: /{X}/{A}/{B}/
        //   D: /{X}/{A}/{B}/{C}/
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var a = TestData.NewPage(space, "a", root);
        var b = TestData.NewPage(space, "b", a);
        var c = TestData.NewPage(space, "c", b);
        var d = TestData.NewPage(space, "d", c);
        var x = TestData.NewPage(space, "x"); // unrelated top-level destination

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, a, b, c, d, x);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(a.Id, x.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);

        using var readContext = CreateContext();
        var reloadedA = readContext.Pages.Single(p => p.Id == a.Id);
        var reloadedB = readContext.Pages.Single(p => p.Id == b.Id);
        var reloadedC = readContext.Pages.Single(p => p.Id == c.Id);
        var reloadedD = readContext.Pages.Single(p => p.Id == d.Id);

        Assert.Equal($"/{x.Id}/", reloadedA.AncestorPath);
        Assert.Equal($"/{x.Id}/{a.Id}/", reloadedB.AncestorPath);
        Assert.Equal($"/{x.Id}/{a.Id}/{b.Id}/", reloadedC.AncestorPath);
        Assert.Equal($"/{x.Id}/{a.Id}/{b.Id}/{c.Id}/", reloadedD.AncestorPath);

        // The subtree's own internal shape (who's a child of whom) is untouched - only
        // the shared prefix above the moved page changed.
        Assert.Equal(a.Id, reloadedB.ParentPageId);
        Assert.Equal(b.Id, reloadedC.ParentPageId);
        Assert.Equal(c.Id, reloadedD.ParentPageId);
    }

    [Fact]
    public async Task MovePage_DescendantAncestorIds_ParseCorrectlyAfterRewrite()
    {
        // Cross-check via Page.GetAncestorIds() (Core) rather than string comparison
        // alone, since that parsed list is what permission checks actually consume.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var a = TestData.NewPage(space, "a");
        var b = TestData.NewPage(space, "b", a);
        var x = TestData.NewPage(space, "x");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(a, b, x);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        await service.MovePageAsync(new MovePageRequest(a.Id, x.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);

        using var readContext = CreateContext();
        var reloadedB = readContext.Pages.Single(p => p.Id == b.Id);

        Assert.Equal(new[] { x.Id, a.Id }, reloadedB.GetAncestorIds());
    }

    [Fact]
    public async Task MovePage_IntoOwnSubtree_ReturnsValidationError_AndPathUnchanged()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");
        var child = TestData.NewPage(space, "child", parent);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(parent, child);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(parent.Id, child.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);

        using var verifyContext = CreateContext();
        Assert.Equal("/", verifyContext.Pages.Single(p => p.Id == parent.Id).AncestorPath);
    }

    [Fact]
    public async Task MovePage_ForbiddenAtDestination_ReturnsForbidden_AndDoesNotMutateAnything()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var oldParent = TestData.NewPage(space, "old-parent");
        var restrictedParent = TestData.NewPage(space, "restricted-parent");
        var page = TestData.NewPage(space, "page", oldParent);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(oldParent, restrictedParent, page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.AccessRules.Add(ViewRestriction(restrictedParent.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(page.Id, restrictedParent.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);

        using var verifyContext = CreateContext();
        var reloaded = verifyContext.Pages.Single(p => p.Id == page.Id);
        Assert.Equal($"/{oldParent.Id}/", reloaded.AncestorPath);
        Assert.Equal(oldParent.Id, reloaded.ParentPageId);
    }

    [Fact]
    public async Task MovePage_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var parent = TestData.NewPage(space, "parent");
        var page = TestData.NewPage(space, "page");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(parent, page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.MovePageAsync(
            new MovePageRequest(page.Id, parent.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    // --- Delete: design.md §6.4.1 subtree cascade -------------------------------------

    [Fact]
    public async Task DeletePage_SingleLeaf_SetsTombstoneFieldsAndReportsCountOne()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.DeletePageAsync(new DeletePageRequest(page.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(page.Id, result.Value.RootPageId);
        Assert.Equal(1, result.Value.DeletedPageCount);

        using var readContext = CreateContext();
        Assert.False(readContext.Pages.Any(p => p.Id == page.Id)); // hidden by the soft-delete query filter
        var reloaded = readContext.Pages.IgnoreQueryFilters().Single(p => p.Id == page.Id);
        Assert.True(reloaded.IsDeleted);
        Assert.Equal(actor.Id, reloaded.DeletedByUserId);
        Assert.NotNull(reloaded.DeletedAtUtc);
    }

    [Fact]
    public async Task DeletePage_StampsSameDeleteBatchIdAcrossSubtree_AndRestoreClearsIt()
    {
        // Direct check of the correlation mechanism itself (DeleteBatchId), not just
        // the observable count/flags behavior the other tests exercise - this was
        // switched from a (DeletedAtUtc, DeletedByUserId) heuristic specifically because
        // that heuristic could merge two independent same-millisecond deletes.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        using (var afterDelete = CreateContext())
        {
            var reloadedRoot = afterDelete.Pages.IgnoreQueryFilters().Single(p => p.Id == root.Id);
            var reloadedChild = afterDelete.Pages.IgnoreQueryFilters().Single(p => p.Id == child.Id);
            Assert.NotNull(reloadedRoot.DeleteBatchId);
            Assert.Equal(reloadedRoot.DeleteBatchId, reloadedChild.DeleteBatchId);
        }

        await service.RestorePageAsync(new RestorePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        using var afterRestore = CreateContext();
        Assert.Null(afterRestore.Pages.Single(p => p.Id == root.Id).DeleteBatchId);
        Assert.Null(afterRestore.Pages.Single(p => p.Id == child.Id).DeleteBatchId);
    }

    [Fact]
    public async Task DeletePage_WithLiveDescendants_CascadesToWholeSubtree_AsOneAuditedOperation()
    {
        // design.md §6.4.1: "delete cascades to the whole subtree, as one audited
        // operation" - soft-deleting a parent while leaving children live would orphan them.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);
        var grandchild = TestData.NewPage(space, "grandchild", child);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child, grandchild);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.DeletedPageCount);

        using var readContext = CreateContext();
        var deletedPages = readContext.Pages.IgnoreQueryFilters()
            .Where(p => p.Id == root.Id || p.Id == child.Id || p.Id == grandchild.Id)
            .ToList();
        Assert.All(deletedPages, p => Assert.True(p.IsDeleted));
        Assert.All(deletedPages, p => Assert.Equal(deletedPages[0].DeletedAtUtc, p.DeletedAtUtc)); // one operation, one timestamp

        // One audit event for the whole operation, not three.
        Assert.Single(readContext.AuditEvents, e => e.Action == "page.delete");
    }

    [Fact]
    public async Task DeletePage_AlreadyIndependentlyDeletedDescendant_IsLeftUntouched_AndNotCounted()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);
        var independentlyDeletedAt = DateTime.UtcNow.AddDays(-1);
        var alreadyGone = TestData.NewPage(space, "already-gone", child);
        alreadyGone.IsDeleted = true;
        alreadyGone.DeletedAtUtc = independentlyDeletedAt;
        alreadyGone.DeletedByUserId = Guid.NewGuid(); // some other, unrelated actor/operation

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child, alreadyGone);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.DeletedPageCount); // root + child, NOT the already-gone page

        using var readContext = CreateContext();
        var reloadedGone = readContext.Pages.IgnoreQueryFilters().Single(p => p.Id == alreadyGone.Id);
        Assert.Equal(independentlyDeletedAt, reloadedGone.DeletedAtUtc); // untouched by this operation
    }

    [Fact]
    public async Task DeletePage_EscalationCase_CanEditParentButNotRestrictedChild_RefusesTheWholeDelete()
    {
        // design.md §6.4.1: "without [a per-page check], deleting a parent is a way to
        // destroy restricted descendants you could never have edited directly."
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var restrictedChild = TestData.NewPage(space, "restricted-child", root);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, restrictedChild);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.AccessRules.Add(ViewRestriction(restrictedChild.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        // EditorPrincipal() is not in "top-secret", so it can edit `root` but not `restrictedChild`.
        var result = await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<SubtreeOperationForbiddenError>(result.Error);
        Assert.Equal(1, error.BlockedPageCount); // reports a count, never which page

        // Refused as a whole: neither page was touched, not even the one the actor
        // could have deleted on its own.
        using var verifyContext = CreateContext();
        Assert.False(verifyContext.Pages.IgnoreQueryFilters().Single(p => p.Id == root.Id).IsDeleted);
        Assert.False(verifyContext.Pages.IgnoreQueryFilters().Single(p => p.Id == restrictedChild.Id).IsDeleted);
    }

    [Fact]
    public async Task DeletePage_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.DeletePageAsync(new DeletePageRequest(page.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    // --- Restore: reverses a subtree delete -------------------------------------------

    [Fact]
    public async Task RestorePage_BringsBackExactlyTheCascadeDeletedSet()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var deleteResult = await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(deleteResult.IsSuccess);

        var restoreResult = await service.RestorePageAsync(new RestorePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(restoreResult.IsSuccess);
        Assert.Equal(2, restoreResult.Value.RestoredPageCount);

        using var readContext = CreateContext();
        Assert.False(readContext.Pages.Single(p => p.Id == root.Id).IsDeleted);
        Assert.False(readContext.Pages.Single(p => p.Id == child.Id).IsDeleted);
        Assert.Single(readContext.AuditEvents, e => e.Action == "page.restore");
    }

    [Fact]
    public async Task RestorePage_DoesNotResurrectIndependentlyDeletedDescendant()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);

        // `child` is deleted independently, BEFORE the cascading delete of `root`.
        var childDeleteResult = await service.DeletePageAsync(new DeletePageRequest(child.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(childDeleteResult.IsSuccess);

        var rootDeleteResult = await service.DeletePageAsync(new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(rootDeleteResult.IsSuccess);
        Assert.Equal(1, rootDeleteResult.Value.DeletedPageCount); // only root - child was already gone

        var restoreResult = await service.RestorePageAsync(new RestorePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(restoreResult.IsSuccess);
        Assert.Equal(1, restoreResult.Value.RestoredPageCount); // only root comes back

        using var readContext = CreateContext();
        Assert.False(readContext.Pages.Single(p => p.Id == root.Id).IsDeleted);
        Assert.True(readContext.Pages.IgnoreQueryFilters().Single(p => p.Id == child.Id).IsDeleted); // still gone
    }

    // --- Restore revision -------------------------------------------------------------

    [Fact]
    public async Task RestoreRevision_CreatesNewRevisionWithOldContent_WithoutMutatingHistory()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.Title = "Current Title";
        page.CurrentContent = "# Current";
        page.CurrentRevisionNumber = 2;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id, RevisionNumber = 1, Title = "Old Title", Content = "# Old",
            AuthorUserId = actor.Id, CreatedAtUtc = DateTime.UtcNow,
        });
        context.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id, RevisionNumber = 2, Title = "Current Title", Content = "# Current",
            AuthorUserId = actor.Id, CreatedAtUtc = DateTime.UtcNow,
        });
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.RestoreRevisionAsync(
            new RestoreRevisionRequest(page.Id, RevisionNumberToRestore: 1, ExpectedCurrentRevisionNumber: 2),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("Old Title", result.Value.Title);
        Assert.Equal("# Old", result.Value.CurrentContent);
        Assert.Equal(3, result.Value.CurrentRevisionNumber);

        using var readContext = CreateContext();
        var revisions = readContext.PageRevisions.Where(r => r.PageId == page.Id).OrderBy(r => r.RevisionNumber).ToList();
        Assert.Equal(3, revisions.Count);
        Assert.Equal("Old Title", revisions[0].Title); // original revision 1 untouched
        Assert.Equal("Old Title", revisions[2].Title); // new revision 3 carries the restored content
    }

    [Fact]
    public async Task RestoreRevision_StaleCurrentRevision_ReturnsStaleRevisionError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 5;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id, RevisionNumber = 1, Title = "Old", Content = "# Old",
            AuthorUserId = actor.Id, CreatedAtUtc = DateTime.UtcNow,
        });
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.RestoreRevisionAsync(
            new RestoreRevisionRequest(page.Id, RevisionNumberToRestore: 1, ExpectedCurrentRevisionNumber: 3),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<StaleRevisionError>(result.Error);
    }

    // --- Replica read-only, remaining page-mutation paths (design.md §6.4/§12) --------
    // Create/Move/Delete replica refusals are covered above; these close out the other
    // three IPageService mutations so every path is proven to sit beneath the replica
    // invariant, and pin that the error carries the origin instance id the web's
    // ReadOnlyReplicaDialog renders.

    [Fact]
    public async Task UpdatePageContent_OnReplicaSpace_ReturnsReadOnlyReplicaError_WithOriginInstanceId()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 1;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(page.Id, ExpectedRevisionNumber: 1, "New Title", "# New", null),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ReadOnlyReplicaError>(result.Error);
        Assert.Equal(space.Id, error.SpaceId);
        Assert.Equal("some-other-instance", error.OriginInstanceId);
    }

    [Fact]
    public async Task RestorePage_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        page.IsDeleted = true;
        page.DeletedAtUtc = DateTime.UtcNow;
        page.DeleteBatchId = Guid.NewGuid();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.RestorePageAsync(new RestorePageRequest(page.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    [Fact]
    public async Task RestoreRevision_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 2;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(new PageRevision
        {
            PageId = page.Id, RevisionNumber = 1, Title = "Old", Content = "# Old",
            AuthorUserId = actor.Id, CreatedAtUtc = DateTime.UtcNow,
        });
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var result = await service.RestoreRevisionAsync(
            new RestoreRevisionRequest(page.Id, RevisionNumberToRestore: 1, ExpectedCurrentRevisionNumber: 2),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }
}
