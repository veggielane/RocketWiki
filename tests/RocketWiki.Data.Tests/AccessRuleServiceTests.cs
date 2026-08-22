using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §6.5.2/§7: rule changes require that space's space-admin OR instance
/// `admin`, and must record complete before/after state for every create/update/delete,
/// since AuditEvent is now the only record of rule history (temporal tables were
/// rejected - data-model.md). The instance-admin arm is covered separately below - it is
/// the bootstrap/recovery path for a space that has zero grants, not a routine bypass.
/// The replay test at the bottom is the property that actually matters here: it runs the
/// real service against a real (SQLite) database and proves the audit rows it produces
/// can reconstruct the correct rule state at each intermediate point, not just the final one.
/// </summary>
public class AccessRuleServiceTests : SqliteTestBase
{
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal AdminPrincipal() => Principal.Create("admin-sub", new[] { "space-admins" });

    private static Principal EditorOnlyPrincipal() => Principal.Create("editor-sub", new[] { "engineering" });

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

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "group": "engineering" }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    // --- Create -------------------------------------------------------------------

    [Fact]
    public async Task Create_SpaceGrant_Succeeds_AuditRecordsNullBeforeAndFullAfter()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.Viewer, null, """{ "everyone": true }"""),
            AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(SpaceRole.Viewer, result.Value.Role);

        var auditEvent = context.AuditEvents.Single(e => e.Action == "permission.change");
        Assert.Contains("\"before\":null", auditEvent.DetailsJson);
        Assert.Contains(result.Value.Id.ToString(), auditEvent.DetailsJson);
    }

    [Fact]
    public async Task Create_PageRestriction_Succeeds()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
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
            AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(PageAction.View, result.Value.Action);
        Assert.Equal(page.Id, result.Value.PageId);
    }

    [Fact]
    public async Task Create_WrongColumnsForKind_ReturnsValidationError()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        // SpaceGrant but with PageId set instead of SpaceId - invalid shape.
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, null, Guid.NewGuid(), SpaceRole.Viewer, null, """{ "everyone": true }"""),
            AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task Create_MalformedExpression_ReturnsValidationError_RuleNeverSaved()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.Viewer, null, """{ "not": "a valid shape" }"""),
            AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(1, context.AccessRules.Count()); // only the seeded admin grant - the malformed one was never saved
    }

    [Fact]
    public async Task Create_ByNonSpaceAdmin_ReturnsForbidden_EvenForAnEditor()
    {
        // design.md §6.5: managing a space's own rules requires space-admin - an editor
        // (who can change page content) has no rule-management rights by default.
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.Viewer, null, """{ "everyone": true }"""),
            EditorOnlyPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task Create_NonExistentSpace_ReturnsNotFound()
    {
        using var context = CreateContext();
        var service = new AccessRuleService(context);

        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, Guid.NewGuid(), null, SpaceRole.Viewer, null, """{ "everyone": true }"""),
            AdminPrincipal(), isInstanceAdmin: false, Guid.NewGuid(), AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<NotFoundError>(result.Error);
    }

    // --- The instance-admin recovery arm (§6.5.2) --------------------------------------

    /// <summary>
    /// design.md §6.5.2: this is the bootstrap-deadlock scenario itself, proven directly -
    /// a space with ZERO grants (no SpaceAdminGrant seeded at all) means space-admin is
    /// false for every principal, so without the instance-admin arm nobody could ever
    /// create this space's first rule. isInstanceAdmin: true is the only thing that makes
    /// this succeed here.
    /// </summary>
    [Fact]
    public async Task Create_OnSpaceWithZeroGrants_ByInstanceAdmin_Succeeds()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace(); // no grants seeded at all

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.SpaceAdmin, null, """{ "everyone": true }"""),
            AdminPrincipal(), isInstanceAdmin: true, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
    }

    /// <summary>Without EITHER arm, the space is exactly as unadministrable as before §6.5.2 - the escape hatch doesn't turn into a bypass for every caller.</summary>
    [Fact]
    public async Task Create_OnSpaceWithZeroGrants_WithoutInstanceAdmin_ReturnsForbidden()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.SpaceAdmin, null, """{ "everyone": true }"""),
            AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task Update_ByInstanceAdmin_WithoutSpaceAdminGrant_Succeeds()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
        var existingRule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = admin.Id,
        };

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(existingRule); // no SpaceAdminGrant seeded - only the instance-admin arm can get in
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.UpdateAsync(
            new UpdateAccessRuleRequest(existingRule.Id, """{ "group": "engineering" }""", SpaceRole.Editor, null),
            EditorOnlyPrincipal(), isInstanceAdmin: true, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Delete_ByInstanceAdmin_WithoutSpaceAdminGrant_Succeeds()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
        var existingRule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = admin.Id,
        };

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(existingRule); // this deliberately leaves the space with zero grants once removed
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.DeleteAsync(new DeleteAccessRuleRequest(existingRule.Id), EditorOnlyPrincipal(), isInstanceAdmin: true, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.False(context.AccessRules.Any(r => r.Id == existingRule.Id));
    }

    // --- Update -------------------------------------------------------------------

    [Fact]
    public async Task Update_Succeeds_AuditRecordsFullBeforeAndAfter()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
        var existingRule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = admin.Id,
        };

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.AccessRules.Add(existingRule);
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.UpdateAsync(
            new UpdateAccessRuleRequest(existingRule.Id, """{ "group": "engineering" }""", SpaceRole.Editor, null),
            AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(SpaceRole.Editor, result.Value.Role);
        Assert.Equal("""{ "group": "engineering" }""", result.Value.ExpressionJson);

        var auditEvent = context.AuditEvents.Single(e => e.Action == "permission.change");
        Assert.Contains("everyone", auditEvent.DetailsJson); // old expression present in "before"
        Assert.Contains("engineering", auditEvent.DetailsJson); // new expression present in "after"
        Assert.DoesNotContain("\"before\":null", auditEvent.DetailsJson); // this is an update, not a creation
    }

    [Fact]
    public async Task Update_ByNonSpaceAdmin_ReturnsForbidden()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
        var existingRule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = admin.Id,
        };

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.AccessRules.Add(existingRule);
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.UpdateAsync(
            new UpdateAccessRuleRequest(existingRule.Id, """{ "group": "engineering" }""", SpaceRole.Editor, null),
            EditorOnlyPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    // --- Delete -------------------------------------------------------------------

    [Fact]
    public async Task Delete_Succeeds_RemovesRow_AuditRecordsFullBeforeAndNullAfter()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
        var existingRule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = admin.Id,
        };

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.AccessRules.Add(existingRule);
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.DeleteAsync(new DeleteAccessRuleRequest(existingRule.Id), AdminPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.False(context.AccessRules.Any(r => r.Id == existingRule.Id));

        var auditEvent = context.AuditEvents.Single(e => e.Action == "permission.change");
        Assert.Contains("everyone", auditEvent.DetailsJson); // full "before" state present
        Assert.Contains("\"after\":null", auditEvent.DetailsJson);
    }

    [Fact]
    public async Task Delete_ByNonSpaceAdmin_ReturnsForbidden_RuleNotRemoved()
    {
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();
        var existingRule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = admin.Id,
        };

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.AccessRules.Add(existingRule);
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var result = await service.DeleteAsync(new DeleteAccessRuleRequest(existingRule.Id), EditorOnlyPrincipal(), isInstanceAdmin: false, admin.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
        Assert.True(context.AccessRules.Any(r => r.Id == existingRule.Id));
    }

    // --- The compliance property: replay through the REAL service and database -----

    [Fact]
    public async Task Replay_UsingRealServiceAndPersistedAuditRows_MatchesActualStateAtEachIntermediatePoint()
    {
        // design.md §7: "reconstruct the rule set at any past instant by replay...
        // enforced by test." Each replay call below happens strictly between two
        // service operations in real wall-clock time, so it reconstructs state from
        // exactly the audit rows that exist at that point - no artificial/controlled
        // timestamps needed, and no risk of a later event's timestamp tying with an
        // earlier one and leaking into an "intermediate" snapshot.
        var admin = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(admin);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new AccessRuleService(context);
        var principal = AdminPrincipal();

        // Step 1: create a Viewer grant.
        var createResult = await service.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.SpaceGrant, space.Id, null, SpaceRole.Viewer, null, """{ "everyone": true }"""),
            principal, isInstanceAdmin: false, admin.Id, AuditCtx);
        Assert.True(createResult.IsSuccess);
        var ruleId = createResult.Value.Id;

        var afterCreate = ReplayNow(context);
        Assert.True(afterCreate.ContainsKey(ruleId));
        Assert.Equal(SpaceRole.Viewer, afterCreate[ruleId].Role);
        Assert.Equal("""{ "everyone": true }""", afterCreate[ruleId].ExpressionJson);

        // Step 2: promote it to Editor with a narrower expression.
        var updateResult = await service.UpdateAsync(
            new UpdateAccessRuleRequest(ruleId, """{ "group": "engineering" }""", SpaceRole.Editor, null),
            principal, isInstanceAdmin: false, admin.Id, AuditCtx);
        Assert.True(updateResult.IsSuccess);

        var afterUpdate = ReplayNow(context);
        Assert.True(afterUpdate.ContainsKey(ruleId));
        Assert.Equal(SpaceRole.Editor, afterUpdate[ruleId].Role);
        Assert.Equal("""{ "group": "engineering" }""", afterUpdate[ruleId].ExpressionJson);

        // The EARLIER snapshot must remain exactly what it was - replaying more events
        // now must never retroactively change what "as of after step 1" reconstructs to.
        Assert.Equal(SpaceRole.Viewer, afterCreate[ruleId].Role);

        // Step 3: delete it.
        var deleteResult = await service.DeleteAsync(new DeleteAccessRuleRequest(ruleId), principal, isInstanceAdmin: false, admin.Id, AuditCtx);
        Assert.True(deleteResult.IsSuccess);

        var afterDelete = ReplayNow(context);
        Assert.False(afterDelete.ContainsKey(ruleId));

        // And the two earlier snapshots are still exactly as they were - a later
        // deletion must not be visible to a reconstruction of an earlier instant.
        Assert.True(afterUpdate.ContainsKey(ruleId));
        Assert.Equal(SpaceRole.Editor, afterUpdate[ruleId].Role);
    }

    private static IReadOnlyDictionary<Guid, AccessRuleSnapshot> ReplayNow(RocketWikiDbContext context)
    {
        var permissionChangeEvents = context.AuditEvents.Where(e => e.Action == "permission.change").ToList();
        return AccessRuleAuditReplay.ReconstructAsOf(permissionChangeEvents, DateTime.UtcNow);
    }
}
