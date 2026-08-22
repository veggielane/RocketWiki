using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §6.4: "comment = requires canView" - a viewer, not just an editor, may
/// comment. Delete is a tombstone (data-model.md): body blanked, node retained so
/// replies keep their parent.
/// </summary>
public class CommentServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal ViewerPrincipal(params string[] groups) => Principal.Create("viewer-sub", groups);

    private static AccessRule ViewerGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Viewer,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "group": "editors" }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

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

    [Fact]
    public async Task AddComment_ByViewerOnly_Succeeds_NoEditRightsNeeded()
    {
        // design.md §6.4: comment requires canView, not canEdit.
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.AddCommentAsync(new AddCommentRequest(page.Id, null, "Great page!"), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("Great page!", result.Value.Body);
        Assert.Contains(context.AuditEvents, e => e.Action == "comment.add");
    }

    [Fact]
    public async Task AddComment_ThreadedReply_LinksToParent()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var parentComment = new Comment { PageId = page.Id, Body = "Original", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(parentComment);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.AddCommentAsync(new AddCommentRequest(page.Id, parentComment.Id, "A reply"), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(parentComment.Id, result.Value.ParentCommentId);
    }

    [Fact]
    public async Task AddComment_ParentOnDifferentPage_ReturnsValidationError()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var pageA = TestData.NewPage(space, "a");
        var pageB = TestData.NewPage(space, "b");
        var commentOnA = new Comment { PageId = pageA.Id, Body = "On A", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.AddRange(pageA, pageB);
        context.Comments.Add(commentOnA);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.AddCommentAsync(new AddCommentRequest(pageB.Id, commentOnA.Id, "Cross-page reply"), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task AddComment_WithoutCanView_ReturnsForbidden()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewRestriction(page.Id, """{ "group": "top-secret" }""")); // no space grant at all -> no role -> canView false regardless
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.AddCommentAsync(new AddCommentRequest(page.Id, null, "Sneaky comment"), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task AddComment_OnReplicaSpace_ReturnsReadOnlyReplicaError()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.AddCommentAsync(new AddCommentRequest(page.Id, null, "Nope"), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    [Fact]
    public async Task EditComment_ByAuthor_Succeeds()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var comment = new Comment { PageId = page.Id, Body = "Original", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(comment);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.EditCommentAsync(new EditCommentRequest(comment.Id, "Updated"), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("Updated", result.Value.Body);
        Assert.NotNull(result.Value.EditedAtUtc);
    }

    [Fact]
    public async Task EditComment_ByNonAuthor_ReturnsForbidden()
    {
        var author = TestData.NewUser();
        var otherUser = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var comment = new Comment { PageId = page.Id, Body = "Original", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.AddRange(author, otherUser);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(comment);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.EditCommentAsync(new EditCommentRequest(comment.Id, "Hijacked"), ViewerPrincipal(), otherUser.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task DeleteComment_ByAuthor_TombstonesBody_KeepsThreadShape()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var parent = new Comment { PageId = page.Id, Body = "Parent", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(parent);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var reply = await service.AddCommentAsync(new AddCommentRequest(page.Id, parent.Id, "A reply"), ViewerPrincipal(), author.Id, AuditCtx);
        Assert.True(reply.IsSuccess);
        var result = await service.DeleteCommentAsync(new DeleteCommentRequest(parent.Id), ViewerPrincipal(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsDeleted);
        Assert.Equal(string.Empty, result.Value.Body);

        using var readContext = CreateContext();
        // Not hidden by any query filter (data-model.md: no global filter on Comment).
        var reloadedParent = readContext.Comments.Single(c => c.Id == parent.Id);
        var reloadedReply = readContext.Comments.Single(c => c.Id == reply.Value.Id);
        Assert.True(reloadedParent.IsDeleted);
        Assert.Equal(parent.Id, reloadedReply.ParentCommentId); // thread shape intact
    }

    [Fact]
    public async Task DeleteComment_ByPageEditor_WhoIsNotAuthor_Succeeds()
    {
        // Judgement call: a page editor may moderate comments on that page.
        var author = TestData.NewUser();
        var moderator = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var comment = new Comment { PageId = page.Id, Body = "Spam", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.AddRange(author, moderator);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(comment);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.DeleteCommentAsync(new DeleteCommentRequest(comment.Id), ViewerPrincipal("editors"), moderator.Id, AuditCtx);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DeleteComment_ByNonAuthorNonEditor_ReturnsForbidden()
    {
        var author = TestData.NewUser();
        var randomViewer = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var comment = new Comment { PageId = page.Id, Body = "Text", AuthorUserId = author.Id, CreatedAtUtc = DateTime.UtcNow };

        using var context = CreateContext();
        context.Users.AddRange(author, randomViewer);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Comments.Add(comment);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new CommentService(context, LocalInstanceId);
        var result = await service.DeleteCommentAsync(new DeleteCommentRequest(comment.Id), ViewerPrincipal(), randomViewer.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }
}
