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
/// design.md §6.5: space create/archive/restore are instance-admin operations (no
/// per-space AccessRule exists yet for a space that isn't there, or that's being torn
/// down instance-wide); rename additionally accepts a space's own space-admin. Both
/// gates are exercised here, along with the audit trail each operation produces.
/// </summary>
public class SpaceServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal AnyPrincipal() => Principal.Create("user-sub", Array.Empty<string>());

    private static Principal SpaceAdminPrincipal() => Principal.Create("space-admin-sub", new[] { "space-admins" });

    private static AccessRule SpaceAdminGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.SpaceAdmin,
        ExpressionJson = """{ "group": "space-admins" }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static InitialSpaceGrant DefaultInitialGrant() => new(SpaceRole.SpaceAdmin, """{ "group": "space-admins" }""");

    // --- Create -------------------------------------------------------------------

    [Fact]
    public async Task Create_ByInstanceAdmin_Succeeds_AsNativeSpace()
    {
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.CreateAsync(
            new CreateSpaceRequest("ENG", "Engineering", "Engineering docs"), DefaultInitialGrant(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("ENG", result.Value.Key);
        Assert.Equal(LocalInstanceId, result.Value.OriginInstanceId);
        Assert.False(result.Value.IsExported);
        Assert.False(result.Value.IsReplicaOf(LocalInstanceId));
        Assert.Contains(context.AuditEvents, e => e.Action == "space.create");
    }

    [Fact]
    public async Task Create_ByNonInstanceAdmin_ReturnsForbidden()
    {
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.CreateAsync(
            new CreateSpaceRequest("ENG", "Engineering", null), DefaultInitialGrant(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task Create_DuplicateKey_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var existing = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(existing);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.CreateAsync(
            new CreateSpaceRequest("ENG", "Another Engineering", null), DefaultInitialGrant(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task Create_InvalidInitialGrantExpression_ReturnsValidationError()
    {
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.CreateAsync(
            new CreateSpaceRequest("ENG", "Engineering", null), new InitialSpaceGrant(SpaceRole.SpaceAdmin, "{ not valid json"), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
        Assert.Empty(context.Spaces); // the whole operation refused, not a space stranded without its grant
    }

    /// <summary>
    /// design.md §6.5.1: this is the bootstrap-deadlock regression test. Before this fix,
    /// a freshly created space had zero AccessRule rows, so AccessRuleService.CreateAsync's
    /// own space-admin check (computed from THIS space's current grants) could never pass
    /// for anyone - not even the person who just created it - because there was nothing to
    /// satisfy yet. Proves the deadlock is broken: the initial grant lands atomically with
    /// the space, and the principal it names can immediately manage further rules on that
    /// space through the real IAccessRuleService, with no instance-admin bypass needed.
    /// </summary>
    [Fact]
    public async Task Create_InitialGrantLandsAtomically_SoItsOwnPrincipalCanImmediatelyManageMoreRules()
    {
        var actor = TestData.NewUser();
        var admin = SpaceAdminPrincipal();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var spaceService = new SpaceService(context, LocalInstanceId);
        var spaceResult = await spaceService.CreateAsync(
            new CreateSpaceRequest("ENG", "Engineering", null), DefaultInitialGrant(), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.True(spaceResult.IsSuccess);
        Assert.Single(context.AccessRules.Where(r => r.SpaceId == spaceResult.Value.Id)); // the one atomic grant, nothing more

        // The space-admin named by the grant just created - not an instance admin -
        // creates a second rule (an editor grant for everyone) through the real service.
        var accessRuleService = new AccessRuleService(context);
        var secondGrant = await accessRuleService.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, spaceResult.Value.Id, null, SpaceRole.Editor, null, """{ "everyone": true }"""),
            admin, isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.True(secondGrant.IsSuccess);
        Assert.Equal(2, context.AccessRules.Count(r => r.SpaceId == spaceResult.Value.Id));
    }

    // --- Rename ---------------------------------------------------------------------

    [Fact]
    public async Task Rename_ByInstanceAdmin_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.RenameAsync(
            new RenameSpaceRequest(space.Id, "New Name", "New description"), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("New Name", result.Value.Name);
        var auditEvent = context.AuditEvents.Single(e => e.Action == "space.rename");
        Assert.Contains("New Name", auditEvent.DetailsJson);
    }

    [Fact]
    public async Task Rename_BySpaceAdmin_WithoutInstanceAdmin_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.RenameAsync(
            new RenameSpaceRequest(space.Id, "Renamed By Space Admin", null), SpaceAdminPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed By Space Admin", result.Value.Name);
    }

    [Fact]
    public async Task Rename_ByNeitherInstanceNorSpaceAdmin_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.RenameAsync(
            new RenameSpaceRequest(space.Id, "Hijacked Name", null), AnyPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task Rename_NonExistentSpace_ReturnsNotFound()
    {
        using var context = CreateContext();
        var service = new SpaceService(context, LocalInstanceId);

        var result = await service.RenameAsync(
            new RenameSpaceRequest(Guid.NewGuid(), "New Name", null), AnyPrincipal(), isInstanceAdmin: true, Guid.NewGuid(), AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<NotFoundError>(result.Error);
    }

    // --- Archive / Restore ------------------------------------------------------------

    [Fact]
    public async Task Archive_ByInstanceAdmin_Succeeds_HiddenByQueryFilter()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.ArchiveAsync(new ArchiveSpaceRequest(space.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.False(context.Spaces.Any(s => s.Id == space.Id));
        Assert.True(context.Spaces.IgnoreQueryFilters().Single(s => s.Id == space.Id).IsDeleted);
        Assert.Contains(context.AuditEvents, e => e.Action == "space.archive");
    }

    [Fact]
    public async Task Archive_BySpaceAdmin_WithoutInstanceAdmin_Succeeds()
    {
        // design.md §6.5.1: archive accepts instance-admin OR that space's own
        // space-admin - "space-admin" means "manage this space", and that now
        // explicitly includes archiving it.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.ArchiveAsync(new ArchiveSpaceRequest(space.Id), SpaceAdminPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Archive_ByNeitherInstanceNorSpaceAdmin_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.ArchiveAsync(new ArchiveSpaceRequest(space.Id), AnyPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task Restore_ByInstanceAdmin_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        await service.ArchiveAsync(new ArchiveSpaceRequest(space.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        var result = await service.RestoreAsync(new RestoreSpaceRequest(space.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.True(context.Spaces.Any(s => s.Id == space.Id));
        Assert.False(context.Spaces.Single(s => s.Id == space.Id).IsDeleted);
    }

    [Fact]
    public async Task Restore_BySpaceAdmin_WithoutInstanceAdmin_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        await service.ArchiveAsync(new ArchiveSpaceRequest(space.Id), SpaceAdminPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        var result = await service.RestoreAsync(new RestoreSpaceRequest(space.Id), SpaceAdminPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Restore_NotCurrentlyArchived_ReturnsNotFound()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.RestoreAsync(new RestoreSpaceRequest(space.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<NotFoundError>(result.Error);
    }
}
