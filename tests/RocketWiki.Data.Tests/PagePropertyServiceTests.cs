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
/// design.md §20. Values are page metadata and follow the label rules (canEdit on that
/// page, replica read-only); the key registry is instance-admin vocabulary and follows
/// the custom-emoji rules. The two facts worth the most here are the ones a reviewer
/// would want proven rather than asserted in a doc comment: deleting a key that pages
/// are using is refused with the count, and "Owner"/"owner" cannot both be registered
/// regardless of which provider the test tier happens to run on.
/// </summary>
public class PagePropertyServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal EditorPrincipal() => Principal.Create("editor-sub", Array.Empty<string>());
    private static Principal ViewerOnlyPrincipal() => Principal.Create("viewer-sub", Array.Empty<string>());

    private static AccessRule Grant(Guid spaceId, SpaceRole role) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = role,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static PagePropertyKey NewKey(string key, int sortOrder = 0) => new()
    {
        Key = key,
        KeyNormalized = PagePropertyKey.Normalize(key),
        SortOrder = sortOrder,
        CreatedAtUtc = DateTime.UtcNow,
    };

    // --- Values: permission matrix ----------------------------------------------------

    [Fact]
    public async Task Set_ByEditor_Succeeds_AndAuditsTheValue()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner", sortOrder: 3);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "Ada Lovelace"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(new PagePropertyValue(key.Id, "Owner", "Ada Lovelace", 3), result.Value);

        var audit = context.AuditEvents.Single(e => e.Action == "page.property.set");
        Assert.Equal(AuditSubjectType.Page, audit.SubjectType);
        Assert.Equal(page.Id, audit.SubjectId);
        Assert.Contains("Ada Lovelace", audit.DetailsJson);
    }

    [Fact]
    public async Task Set_ByViewerOnly_ReturnsForbidden_AndWritesNothing()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Viewer));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "Ada Lovelace"), ViewerOnlyPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
        Assert.Empty(context.PageProperties);
    }

    [Fact]
    public async Task Set_UnknownPage_ReturnsNotFound()
    {
        var actor = TestData.NewUser();
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.PagePropertyKeys.Add(key);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var missingPageId = Guid.NewGuid();
        var result = await service.SetAsync(
            new SetPagePropertyRequest(missingPageId, key.Id, "x"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal(missingPageId, Assert.IsType<NotFoundError>(result.Error).Id);
    }

    [Fact]
    public async Task Set_UnknownKey_ReturnsNotFound()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var missingKeyId = Guid.NewGuid();
        var result = await service.SetAsync(
            new SetPagePropertyRequest(page.Id, missingKeyId, "x"), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal(missingKeyId, Assert.IsType<NotFoundError>(result.Error).Id);
    }

    [Fact]
    public async Task Set_Twice_OverwritesRatherThanAddingASecondRow()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        Assert.True((await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "first"), EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);
        Assert.True((await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "second"), EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        // The composite PK is (PageId, PagePropertyKeyId): one value per key per page.
        var stored = Assert.Single(context.PageProperties.ToList());
        Assert.Equal("second", stored.Value);
        // Both writes are audited - the record is of what happened, not of final state.
        Assert.Equal(2, context.AuditEvents.Count(e => e.Action == "page.property.set"));
    }

    [Fact]
    public async Task Set_EmptyValue_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "   "), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
        Assert.Empty(context.PageProperties);
    }

    [Fact]
    public async Task Set_ValueLongerThanTheColumn_ReturnsValidationError()
    {
        // SQLite does not enforce declared lengths (SqliteTestBase's own note), so
        // without the application check this would silently store here and blow up on
        // SQL Server - the tier disagreement the service refuses to allow.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, new string('x', PagePropertyService.MaxValueLength + 1)),
            EditorPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task Remove_ByEditor_DeletesTheRow_AndAuditsWithoutTheValue()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.PageProperties.Add(new PageProperty
        {
            PageId = page.Id, PagePropertyKeyId = key.Id, Value = "Ada Lovelace", UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = actor.Id,
        });
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.RemoveAsync(
            new RemovePagePropertyRequest(page.Id, key.Id), EditorPrincipal(), actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(key.Id, result.Value);
        Assert.Empty(context.PageProperties);

        var audit = context.AuditEvents.Single(e => e.Action == "page.property.remove");
        Assert.Contains("Owner", audit.DetailsJson);
        Assert.DoesNotContain("Ada Lovelace", audit.DetailsJson);
    }

    [Fact]
    public async Task Remove_PropertyNotSet_ReturnsValidationError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.RemoveAsync(
            new RemovePagePropertyRequest(page.Id, key.Id), EditorPrincipal(), actor.Id, AuditCtx);

        // Not silently accepted: the same non-idempotent stance DetachLabelAsync takes.
        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task Remove_ByViewerOnly_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        context.PageProperties.Add(new PageProperty
        {
            PageId = page.Id, PagePropertyKeyId = key.Id, Value = "Ada", UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = actor.Id,
        });
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Viewer));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.RemoveAsync(
            new RemovePagePropertyRequest(page.Id, key.Id), ViewerOnlyPrincipal(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);
        Assert.Single(context.PageProperties.ToList());
    }

    // --- Replica read-only (design.md §6.4/§12) ---------------------------------------

    [Fact]
    public async Task SetAndRemove_OnReplicaSpace_ReturnReadOnlyReplicaError()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PagePropertyKeys.Add(key);
        // Already set (as a sync import would leave it), so the remove path gets past
        // its own not-set check and the replica invariant is what refuses it.
        context.PageProperties.Add(new PageProperty
        {
            PageId = page.Id, PagePropertyKeyId = key.Id, Value = "mirrored", UpdatedAtUtc = DateTime.UtcNow,
        });
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);

        var set = await service.SetAsync(
            new SetPagePropertyRequest(page.Id, key.Id, "local edit"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.False(set.IsSuccess);
        Assert.Equal("some-other-instance", Assert.IsType<ReadOnlyReplicaError>(set.Error).OriginInstanceId);

        var remove = await service.RemoveAsync(
            new RemovePagePropertyRequest(page.Id, key.Id), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.False(remove.IsSuccess);
        Assert.IsType<ReadOnlyReplicaError>(remove.Error);
    }

    // --- Registry: the instance-admin gate, both ways ---------------------------------

    [Fact]
    public async Task CreateKey_ByInstanceAdmin_Succeeds_AndAudits()
    {
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.CreateKeyAsync(
            new CreatePagePropertyKeyRequest("  Review Date  ", "When this page was last reviewed"),
            isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("Review Date", result.Value.Key); // trimmed
        Assert.Equal("review date", result.Value.KeyNormalized);

        var audit = context.AuditEvents.Single(e => e.Action == "property_key.create");
        Assert.Null(audit.SubjectType); // registry vocabulary has no AuditSubjectType
        Assert.Contains("Review Date", audit.DetailsJson);
    }

    [Fact]
    public async Task CreateKey_ByNonAdmin_ReturnsForbidden_WithTheNormalizedReason()
    {
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.CreateKeyAsync(
            new CreatePagePropertyKeyRequest("Owner", null), isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("instance admin required", Assert.IsType<ForbiddenError>(result.Error).Reason);
        Assert.Empty(context.PagePropertyKeys);
    }

    [Fact]
    public async Task CreateKey_DifferingOnlyByCase_IsRejectedOnEveryProvider()
    {
        // The reason KeyNormalized exists: SQL Server's default collation is
        // case-insensitive and SQLite's is not, so leaving this to a unique index on
        // the raw Key would mean this test passes here and the rule differs in
        // production. Normalizing in the application makes the answer identical.
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        Assert.True((await service.CreateKeyAsync(
            new CreatePagePropertyKeyRequest("Owner", null), isInstanceAdmin: true, actor.Id, AuditCtx)).IsSuccess);

        var duplicate = await service.CreateKeyAsync(
            new CreatePagePropertyKeyRequest("owner", null), isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(duplicate.IsSuccess);
        Assert.IsType<ValidationError>(duplicate.Error);
        Assert.Single(context.PagePropertyKeys.ToList());
    }

    [Fact]
    public void CreateKey_KeyNormalizedUniqueIndex_IsEnforcedByTheDatabaseToo()
    {
        // Belt and braces: even if a future caller bypassed the service check, the
        // index refuses the row. (SQLite genuinely enforces unique indexes.)
        using var context = CreateContext();
        context.PagePropertyKeys.Add(NewKey("Owner"));
        context.SaveChanges();

        context.PagePropertyKeys.Add(new PagePropertyKey
        {
            Key = "owner", KeyNormalized = "owner", CreatedAtUtc = DateTime.UtcNow,
        });
        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public async Task DeleteKey_ByNonAdmin_ReturnsForbidden()
    {
        var actor = TestData.NewUser();
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.PagePropertyKeys.Add(key);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.DeleteKeyAsync(key.Id, isInstanceAdmin: false, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("instance admin required", Assert.IsType<ForbiddenError>(result.Error).Reason);
        Assert.Single(context.PagePropertyKeys.ToList());
    }

    [Fact]
    public async Task DeleteKey_UnknownKey_ReturnsNotFound()
    {
        var actor = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var missing = Guid.NewGuid();
        var result = await service.DeleteKeyAsync(missing, isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal(missing, Assert.IsType<NotFoundError>(result.Error).Id);
    }

    [Fact]
    public async Task DeleteKey_InUse_IsRefused_AndNamesTheUsageCount()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var pageA = TestData.NewPage(space, "a");
        var pageB = TestData.NewPage(space, "b");
        var key = NewKey("Owner");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.AddRange(pageA, pageB);
        context.PagePropertyKeys.Add(key);
        context.PageProperties.AddRange(
            new PageProperty { PageId = pageA.Id, PagePropertyKeyId = key.Id, Value = "Ada", UpdatedAtUtc = DateTime.UtcNow },
            new PageProperty { PageId = pageB.Id, PagePropertyKeyId = key.Id, Value = "Grace", UpdatedAtUtc = DateTime.UtcNow });
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.DeleteKeyAsync(key.Id, isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains("2", error.Message); // the usage count, never a list of pages
        // Nothing destroyed: both the key and both values survive.
        Assert.Single(context.PagePropertyKeys.ToList());
        Assert.Equal(2, context.PageProperties.Count());
    }

    [Fact]
    public async Task DeleteKey_Unused_Succeeds_AndAudits()
    {
        var actor = TestData.NewUser();
        var key = NewKey("Obsolete");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.PagePropertyKeys.Add(key);
        context.SaveChanges();

        var service = new PagePropertyService(context, LocalInstanceId);
        var result = await service.DeleteKeyAsync(key.Id, isInstanceAdmin: true, actor.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(key.Id, result.Value);
        Assert.Empty(context.PagePropertyKeys);

        var audit = context.AuditEvents.Single(e => e.Action == "property_key.delete");
        Assert.Null(audit.SubjectType);
        Assert.Contains("Obsolete", audit.DetailsJson);
    }
}
