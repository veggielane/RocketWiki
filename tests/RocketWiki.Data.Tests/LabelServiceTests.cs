using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

public class LabelServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal EditorPrincipal(params string[] groups) => Principal.Create("editor-sub", groups);
    private static Principal ViewerOnlyPrincipal() => Principal.Create("viewer-sub", Array.Empty<string>());

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
    public async Task CreateLabel_ByEditor_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.CreateLabelAsync(new CreateLabelRequest(space.Id, "how-to"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("how-to", result.Value.Name);
    }

    [Fact]
    public async Task CreateLabel_ByViewerOnly_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.CreateLabelAsync(new CreateLabelRequest(space.Id, "how-to"), ViewerOnlyPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task CreateLabel_DuplicateNameInSpace_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var existing = new Label { SpaceId = space.Id, Name = "how-to" };

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Labels.Add(existing);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.CreateLabelAsync(new CreateLabelRequest(space.Id, "how-to"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task AttachLabel_ByEditor_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var label = new Label { SpaceId = space.Id, Name = "how-to" };

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Labels.Add(label);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.AttachLabelAsync(new AttachLabelRequest(page.Id, label.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Contains(context.AuditEvents, e => e.Action == "label.attach");
    }

    [Fact]
    public async Task AttachLabel_AlreadyAttached_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var label = new Label { SpaceId = space.Id, Name = "how-to" };

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Labels.Add(label);
        context.PageLabels.Add(new PageLabel { PageId = page.Id, LabelId = label.Id });
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.AttachLabelAsync(new AttachLabelRequest(page.Id, label.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task AttachLabel_LabelFromDifferentSpace_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var spaceA = TestData.NewSpace("A");
        var spaceB = TestData.NewSpace("B");
        var page = TestData.NewPage(spaceA);
        var labelFromB = new Label { SpaceId = spaceB.Id, Name = "how-to" };

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.AddRange(spaceA, spaceB);
        context.Pages.Add(page);
        context.Labels.Add(labelFromB);
        context.AccessRules.Add(EditorGrant(spaceA.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.AttachLabelAsync(new AttachLabelRequest(page.Id, labelFromB.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task DetachLabel_ByEditor_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var label = new Label { SpaceId = space.Id, Name = "how-to" };

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Labels.Add(label);
        context.PageLabels.Add(new PageLabel { PageId = page.Id, LabelId = label.Id });
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.DetachLabelAsync(new DetachLabelRequest(page.Id, label.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.False(context.PageLabels.Any(pl => pl.PageId == page.Id && pl.LabelId == label.Id));
    }

    // --- The permission-filtered read: the important part -----------------------------

    [Fact]
    public async Task GetPagesByLabel_ExcludesRestrictedPages_NotJustFromCountButEntirely()
    {
        var space = TestData.NewSpace();
        var visiblePage = TestData.NewPage(space, "visible");
        var restrictedPage = TestData.NewPage(space, "restricted");
        var label = new Label { SpaceId = space.Id, Name = "shared-label" };

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(visiblePage, restrictedPage);
        context.Labels.Add(label);
        context.PageLabels.Add(new PageLabel { PageId = visiblePage.Id, LabelId = label.Id });
        context.PageLabels.Add(new PageLabel { PageId = restrictedPage.Id, LabelId = label.Id });
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.AccessRules.Add(ViewRestriction(restrictedPage.Id, """{ "group": "top-secret" }"""));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.GetPagesByLabelAsync(space.Id, "shared-label", ViewerOnlyPrincipal());

        var resultIds = result.Select(p => p.Id).ToList();
        Assert.Contains(visiblePage.Id, resultIds);
        Assert.DoesNotContain(restrictedPage.Id, resultIds);
        Assert.Single(result); // not two-with-a-placeholder, not a count hint - just the one visible page
    }

    [Fact]
    public async Task GetPagesByLabel_NoSpaceRoleAtAll_ReturnsEmpty()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var label = new Label { SpaceId = space.Id, Name = "shared-label" };

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Labels.Add(label);
        context.PageLabels.Add(new PageLabel { PageId = page.Id, LabelId = label.Id });
        // No AccessRule grant at all for this space.
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.GetPagesByLabelAsync(space.Id, "shared-label", ViewerOnlyPrincipal());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPagesByLabel_NonExistentSpace_ReturnsEmpty()
    {
        using var context = CreateContext();
        var service = new LabelService(context, LocalInstanceId);

        var result = await service.GetPagesByLabelAsync(Guid.NewGuid(), "anything", ViewerOnlyPrincipal());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPagesByLabel_UnknownLabelName_ReturnsEmpty()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(ViewerGrant(space.Id));
        context.SaveChanges();

        var service = new LabelService(context, LocalInstanceId);
        var result = await service.GetPagesByLabelAsync(space.Id, "does-not-exist", ViewerOnlyPrincipal());

        Assert.Empty(result);
    }
}
