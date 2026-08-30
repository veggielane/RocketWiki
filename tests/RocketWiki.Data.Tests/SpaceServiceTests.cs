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

    // --- Homepage ---------------------------------------------------------------------

    /// <summary>
    /// Null is "clear it", not "leave it alone" — a dedicated setter whose entire payload
    /// is the homepage has no other reading, and the alternative would make a space's
    /// first homepage permanent. The audit row carries both ids because the column is a
    /// single mutable value: after the write, nothing else records what it used to be.
    /// </summary>
    [Fact]
    public async Task SetHomepage_SetsThePage_AndNullClearsIt()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        var page = TestData.NewPage(space, "welcome");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);

        var set = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, page.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.True(set.IsSuccess);
        Assert.Equal(page.Id, set.Value.HomepageId);

        var cleared = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, null), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.True(cleared.IsSuccess);
        Assert.Null(cleared.Value.HomepageId);

        using var readContext = CreateContext();
        Assert.Null(readContext.Spaces.Single(s => s.Id == space.Id).HomepageId);

        var audits = context.AuditEvents.Where(e => e.Action == "space.homepage.set").OrderBy(e => e.Id).ToList();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, e => Assert.Equal(AuditSubjectType.Space, e.SubjectType));
        Assert.All(audits, e => Assert.Equal(space.Id, e.SubjectId));
        Assert.Contains(page.Id.ToString(), audits[0].DetailsJson);
        // The clearing row still names what was lost, which is the whole point of
        // carrying the before-state.
        Assert.Contains(page.Id.ToString(), audits[1].DetailsJson);
    }

    /// <summary>
    /// A page in another space and a page id that exists nowhere collapse to one message
    /// deliberately: telling them apart would answer "does this id exist?" for spaces the
    /// caller administers nothing in.
    /// </summary>
    [Fact]
    public async Task SetHomepage_PageInAnotherSpaceOrNoSuchPage_ReturnsValidationError_AndChangesNothing()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        var otherSpace = TestData.NewSpace("OPS");
        var foreignPage = TestData.NewPage(otherSpace, "ops-home");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Spaces.Add(otherSpace);
        context.Pages.Add(foreignPage);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);

        var crossSpace = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, foreignPage.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.False(crossSpace.IsSuccess);
        Assert.Contains("is not a page in space 'ENG'", Assert.IsType<ValidationError>(crossSpace.Error).Message);

        var missingId = Guid.NewGuid();
        var missing = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, missingId), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.False(missing.IsSuccess);
        Assert.Contains("is not a page in space 'ENG'", Assert.IsType<ValidationError>(missing.Error).Message);

        using var readContext = CreateContext();
        Assert.Null(readContext.Spaces.Single(s => s.Id == space.Id).HomepageId);
    }

    /// <summary>
    /// A trashed page is refused with its own message rather than the cross-space one:
    /// the caller administers this space, can see its trash, and "restore it first" is
    /// the actionable answer. Left as a homepage it would be a link to nothing, since
    /// every read path filters deleted pages out.
    /// </summary>
    [Fact]
    public async Task SetHomepage_TrashedPage_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        var page = TestData.NewPage(space, "gone");
        page.IsDeleted = true;
        page.DeletedAtUtc = DateTime.UtcNow;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, page.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Contains("is in the trash", Assert.IsType<ValidationError>(result.Error).Message);
    }

    [Fact]
    public async Task SetHomepage_BySpaceAdmin_WithoutInstanceAdmin_Succeeds()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        var page = TestData.NewPage(space, "welcome");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, page.Id), SpaceAdminPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(page.Id, result.Value.HomepageId);
    }

    [Fact]
    public async Task SetHomepage_ByNeitherInstanceNorSpaceAdmin_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        var page = TestData.NewPage(space, "welcome");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, page.Id), AnyPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task SetHomepage_NonExistentSpace_ReturnsNotFound()
    {
        using var context = CreateContext();
        var service = new SpaceService(context, LocalInstanceId);

        var result = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(Guid.NewGuid(), null), AnyPrincipal(), isInstanceAdmin: true, Guid.NewGuid(), AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<NotFoundError>(result.Error);
    }

    /// <summary>
    /// design.md §12's table puts space lifecycle and identity in the "stays local"
    /// column, so a homepage change on an EXPORTED space journals nothing — each side
    /// chooses its own default page. Pinned rather than left implicit because the absence
    /// of a SyncEventType arm is invisible at the call site, and because a page reference
    /// is the one kind of space metadata someone might reasonably try to sync later: the
    /// high side may not hold the page the low side chose.
    /// </summary>
    [Fact]
    public async Task SetHomepage_OnAnExportedSpace_JournalsNoOutboxEvent()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        space.IsExported = true; // native AND exported: page edits here DO journal
        var page = TestData.NewPage(space, "welcome");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, page.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(context.SyncOutboxEvents.ToList());
        Assert.Equal(0, context.Spaces.Single(s => s.Id == space.Id).LastOutboxSequence);
        // Still audited, though - staying local is about sync, not about the §7 record.
        Assert.Single(context.AuditEvents.Where(e => e.Action == "space.homepage.set"));
    }

    /// <summary>
    /// The other half of the same §12 rule: a replica's admin may choose its default page,
    /// exactly as they may rename or archive it. Deliberately NOT a ReadOnlyReplicaError -
    /// that guard belongs to content writes, which would reach back across the boundary;
    /// this is local curation of a value that never crosses in either direction. Page ids
    /// survive sync, so the replicated page this points at is a real target.
    /// </summary>
    [Fact]
    public async Task SetHomepage_OnAReplicaSpace_IsAllowedAsLocalCuration()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        space.OriginInstanceId = "the-low-side"; // != LocalInstanceId, so IsReplicaOf is true
        var page = TestData.NewPage(space, "replicated");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        Assert.True(space.IsReplicaOf(LocalInstanceId));

        var service = new SpaceService(context, LocalInstanceId);
        var result = await service.SetHomepageAsync(
            new SetSpaceHomepageRequest(space.Id, page.Id), AnyPrincipal(), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(page.Id, result.Value.HomepageId);
    }

    /// <summary>
    /// A key longer than the column must be refused cleanly, not left to blow up in
    /// SaveChanges. SQLite does not enforce declared string lengths, so an unchecked one
    /// stores silently in the test tier and comes back as a raw DbUpdateException in
    /// production — the tier-parity trap PageMarkingService already guards its prefix
    /// against.
    ///
    /// <para>It is met on ordinary data, not adversarial input: Confluence personal-space
    /// keys are <c>~accountId</c> and routinely exceed 32, and the importer passes the
    /// export's key straight through. Down that path nothing catches the exception, so a
    /// half-written import aborts with no report instead of refusing before anything is
    /// created.</para>
    /// </summary>
    [Fact]
    public async Task Create_WithAKeyLongerThanTheColumn_IsAValidationError_NotADbFailure()
    {
        var actor = TestData.NewUser();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var tooLong = "~" + new string('a', 64); // a Confluence personal-space key
        var result = await new SpaceService(context, LocalInstanceId).CreateAsync(
            new CreateSpaceRequest(tooLong, "Personal", null), DefaultInitialGrant(),
            isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
        Assert.Empty(context.Spaces.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task Create_WithANameLongerThanTheColumn_IsAValidationError()
    {
        var actor = TestData.NewUser();
        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var result = await new SpaceService(context, LocalInstanceId).CreateAsync(
            new CreateSpaceRequest("ENG", new string('n', 500), null), DefaultInitialGrant(),
            isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    // --- Export flag (design.md §12) --------------------------------------------------

    /// <summary>
    /// The on-switch §12 always described and nothing implemented: before this, both
    /// writers of <c>Space.IsExported</c> set it to <c>false</c> and no mutation, route or
    /// CLI verb could set it true, so the outbox never journaled anything and
    /// <c>RocketWiki.Sync export --baseline</c> refused every space — while telling the
    /// operator to "flag it exported first". The only route was hand-editing the database.
    ///
    /// <para>Asserted end to end rather than on the flag alone: flipping it is only
    /// meaningful if it is what makes the outbox start journaling, which is the whole
    /// point of the flag.</para>
    /// </summary>
    [Fact]
    public async Task SetExported_ByInstanceAdmin_FlagsTheSpace_AndTheOutboxStartsJournaling()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        space.IsExported = false;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, LocalInstanceId);
        var before = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "before", "Before", "# Before"),
            SpaceAdminPrincipal(), actor.Id, AuditCtx);
        Assert.True(before.IsSuccess);
        Assert.Empty(context.SyncOutboxEvents.ToList()); // not exported yet

        var result = await new SpaceService(context, LocalInstanceId).SetExportedAsync(
            new SetSpaceExportedRequest(space.Id, Exported: true), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.True(context.Spaces.Single(s => s.Id == space.Id).IsExported);
        Assert.Single(context.AuditEvents.Where(e => e.Action == "space.export.enabled"));

        var after = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "after", "After", "# After"),
            SpaceAdminPrincipal(), actor.Id, AuditCtx);
        Assert.True(after.IsSuccess);
        Assert.Single(context.SyncOutboxEvents.ToList());

        // The flag itself never crosses: §12 keeps space lifecycle local, and
        // exported-ness is the LOW side's decision about its own space.
        Assert.DoesNotContain(
            context.SyncOutboxEvents.ToList(),
            e => e.PayloadJson.Contains("exported", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Turning it off is the safe direction and gets the quieter action name —
    /// the same set-vs-downgrade split §21.7 uses, so a reviewer can find every space
    /// somebody opened for export with one query.</summary>
    [Fact]
    public async Task SetExported_Disabling_AuditsUnderItsOwnActionName()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        space.IsExported = true;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var service = new SpaceService(context, LocalInstanceId);
        Assert.True((await service.SetExportedAsync(
            new SetSpaceExportedRequest(space.Id, Exported: false), isInstanceAdmin: true, actor.Id, AuditCtx)).IsSuccess);

        Assert.False(context.Spaces.Single(s => s.Id == space.Id).IsExported);
        Assert.Single(context.AuditEvents.Where(e => e.Action == "space.export.disabled"));
        Assert.Empty(context.AuditEvents.Where(e => e.Action == "space.export.enabled"));
    }

    /// <summary>Idempotent, and deliberately silent: an audit row claiming export was
    /// "enabled" when it already was would put a widening in front of the reviewer §7
    /// writes these rows for.</summary>
    [Fact]
    public async Task SetExported_AlreadyInThatState_SucceedsWithoutAnAuditRow()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        space.IsExported = true;

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var result = await new SpaceService(context, LocalInstanceId).SetExportedAsync(
            new SetSpaceExportedRequest(space.Id, Exported: true), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(context.AuditEvents.ToList()
            .Where(e => e.Action.StartsWith("space.export.", StringComparison.Ordinal)));
    }

    /// <summary>
    /// design.md §12: "Only a native space can be exported; a replica must never emit
    /// sync events for content it doesn't own." The outbox writer already refuses to
    /// journal a replica-flagged-exported space, calling it corrupt state; this refuses
    /// to create that state at all.
    /// </summary>
    [Fact]
    public async Task SetExported_OnAReplicaSpace_IsRefused()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");
        space.OriginInstanceId = "the-low-side"; // != LocalInstanceId, so IsReplicaOf is true

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.SaveChanges();

        var result = await new SpaceService(context, LocalInstanceId).SetExportedAsync(
            new SetSpaceExportedRequest(space.Id, Exported: true), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(result.Error);
        Assert.False(context.Spaces.Single(s => s.Id == space.Id).IsExported);
    }

    /// <summary>Instance admin only — NOT the "or space admin" arm rename/archive share.
    /// Sending a space's content into another security domain is a judgement about the
    /// boundary, not curation of the space.</summary>
    [Fact]
    public async Task SetExported_BySpaceAdminWithoutInstanceAdmin_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace("ENG");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(SpaceAdminGrant(space.Id));
        context.SaveChanges();

        var result = await new SpaceService(context, LocalInstanceId).SetExportedAsync(
            new SetSpaceExportedRequest(space.Id, Exported: true), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
        Assert.False(context.Spaces.Single(s => s.Id == space.Id).IsExported);
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
