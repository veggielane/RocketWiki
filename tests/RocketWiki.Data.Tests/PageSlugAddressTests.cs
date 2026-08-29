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
/// The slug as an ADDRESS: a page is reachable at /spaces/{spaceKey}/{slug}.
///
/// Two consequences follow from putting it in the URL without the hierarchy, and both
/// are tested here rather than assumed. Uniqueness has to widen from (space, parent)
/// to (space), or one address could resolve to two pages. And a slug that a sibling
/// route would swallow has to be refused, or the page is created successfully and is
/// then unreachable forever — a failure with no error message anywhere.
/// </summary>
public class PageSlugAddressTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal EditorPrincipal() => Principal.Create("user-sub", []);

    /// <summary>Mirrors PageServiceTests' helper: goes through the real rule service
    /// (§6.5.2's instance-admin arm) rather than inserting a grant row directly.</summary>
    private static async Task GrantSpaceRoleAsync(
        RocketWikiDbContext context, Guid spaceId, SpaceRole role, Guid actingUserId)
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

    private async Task<(RocketWikiDbContext Context, Space Space, User Actor)> SeedAsync()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await GrantSpaceRoleAsync(context, space.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();
        return (context, space, actor);
    }

    [Theory]
    [InlineData("-")]
    [InlineData(" - ")]
    public async Task CreatePage_WithTheSystemSegmentAsItsSlug_IsRefused(string slug)
    {
        // Every space's system pages live under /spaces/{key}/-/..., so `-` is the one
        // segment a page cannot claim. A page allowed to take it would exist and never
        // be reachable, so it fails at creation while the author can still choose again.
        var (context, space, actor) = await SeedAsync();
        using var _ = context;

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, slug, "Reserved", "# x"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
        Assert.Contains("reserved", ((ValidationError)result.Error!).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("grants")]
    [InlineData("trash")]
    [InlineData("admin-guide")]
    [InlineData("--")]
    public async Task CreatePage_WithASlugThatMerelyLooksSystemish_IsAllowed(string slug)
    {
        // The old design reserved a WORD per space route, so these were all refused.
        // Moving system pages behind /-/ means only that exact segment is reserved —
        // and "--" is a different segment from "-".
        var (context, space, actor) = await SeedAsync();
        using var _ = context;

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, slug, "Fine", "# x"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CreatePage_WithASlugUsedUnderADifferentParent_IsRefused()
    {
        // Legal under the old per-parent rule and NOT legal now: /spaces/{key}/notes
        // has to mean exactly one page.
        var (context, space, actor) = await SeedAsync();
        using var _ = context;
        var parent = TestData.NewPage(space, "parent");
        context.Pages.Add(parent);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var first = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "notes", "Notes", "# a"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(first.IsSuccess);

        var second = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "notes", "Other notes", "# b"),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(second.IsSuccess);
        Assert.IsType<ValidationError>(second.Error);
    }

    [Fact]
    public async Task CreatePage_WithASlugUsedInAnotherSpace_IsAllowed()
    {
        // Uniqueness is per space, not global — /spaces/ENG/notes and /spaces/OPS/notes
        // are different addresses and must both be creatable.
        var (context, space, actor) = await SeedAsync();
        using var _ = context;
        var otherSpace = TestData.NewSpace("OPS");
        context.Spaces.Add(otherSpace);
        await GrantSpaceRoleAsync(context, otherSpace.Id, SpaceRole.Editor, actor.Id);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        Assert.True((await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "notes", "Notes", "# a"),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);
        Assert.True((await service.CreatePageAsync(
            new CreatePageRequest(otherSpace.Id, null, "notes", "Notes", "# b"),
            EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);
    }

    [Fact]
    public async Task FindPageIdBySlug_ResolvesTheLivePage()
    {
        var (context, space, actor) = await SeedAsync();
        using var _ = context;
        var service = new PageService(context, LocalInstanceId);
        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "launch-notes", "Launch notes", "# a"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var readService = new PageReadService(context);
        var found = await readService.FindPageIdBySlugAsync(space.Key, "launch-notes");

        Assert.Equal(created.Value.Id, found);
    }

    [Fact]
    public async Task FindPageIdBySlug_DoesNotResolveADeletedPage()
    {
        // A trashed page's row survives for restore, but its address must not — both so
        // the slug returns to the pool and so the URL stops working the moment the page
        // leaves the tree.
        var (context, space, actor) = await SeedAsync();
        using var _ = context;
        var service = new PageService(context, LocalInstanceId);
        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "gone", "Gone", "# a"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        await service.DeletePageAsync(
            new DeletePageRequest(created.Value.Id), EditorPrincipal(), actor.Id, AuditCtx);

        var readService = new PageReadService(context);
        Assert.Null(await readService.FindPageIdBySlugAsync(space.Key, "gone"));
    }

    [Fact]
    public async Task FindPageIdBySlug_ForAnUnknownSpaceOrSlug_IsNull()
    {
        var (context, space, _) = await SeedAsync();
        using var _c = context;
        var readService = new PageReadService(context);

        Assert.Null(await readService.FindPageIdBySlugAsync("NOSUCHSPACE", "anything"));
        Assert.Null(await readService.FindPageIdBySlugAsync(space.Key, "no-such-slug"));
    }

    [Fact]
    public async Task MovingAPage_DoesNotChangeItsSlug()
    {
        // The whole reason the hierarchy is absent from the URL: a page that moves keeps
        // its address, so every link to it keeps working.
        var (context, space, actor) = await SeedAsync();
        using var _ = context;
        var newParent = TestData.NewPage(space, "new-parent");
        context.Pages.Add(newParent);
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);
        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "stays-put", "Stays put", "# a"),
            EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var moved = await service.MovePageAsync(
            new MovePageRequest(created.Value.Id, newParent.Id, 0), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(moved.IsSuccess);

        var readService = new PageReadService(context);
        Assert.Equal(created.Value.Id, await readService.FindPageIdBySlugAsync(space.Key, "stays-put"));
        Assert.Equal(newParent.Id, (await context.Pages.AsNoTracking()
            .FirstAsync(p => p.Id == created.Value.Id)).ParentPageId);
    }
}
