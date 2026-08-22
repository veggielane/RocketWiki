using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// SQLite genuinely enforces CHECK constraints declared via HasCheckConstraint (unlike
/// HasMaxLength - see SqliteTestBase), so these prove the constraints reject bad rows
/// rather than just existing as unread DDL text in the migration.
/// </summary>
public class CheckConstraintTests : SqliteTestBase
{
    [Fact]
    public void AccessRule_SpaceGrantWithPageIdInsteadOfSpaceId_ViolatesKindColumnPairing()
    {
        using var context = CreateContext();
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            PageId = Guid.NewGuid(), // wrong column for this Kind
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AccessRule_SpaceGrantMissingRole_ViolatesKindColumnPairing()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = null, // required for SpaceGrant
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AccessRule_PageRestrictionWithSpaceIdInsteadOfPageId_ViolatesKindColumnPairing()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            SpaceId = space.Id, // wrong column for this Kind
            Action = PageAction.View,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AccessRule_ValidSpaceGrant_Saves()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        var exception = Record.Exception(() => context.SaveChanges());
        Assert.Null(exception);
    }

    [Fact]
    public void AccessRule_ValidPageRestriction_Saves()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            PageId = page.Id,
            Action = PageAction.View,
            ExpressionJson = """{ "group": "top-secret" }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        var exception = Record.Exception(() => context.SaveChanges());
        Assert.Null(exception);
    }

    [Fact]
    public void Watch_BothSpaceAndPageSet_ViolatesSpaceXorPage()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Watches.Add(new Watch
        {
            UserId = user.Id,
            SpaceId = space.Id,
            PageId = page.Id, // both set - invalid
            CreatedAtUtc = DateTime.UtcNow,
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Watch_NeitherSpaceNorPageSet_ViolatesSpaceXorPage()
    {
        var user = TestData.NewUser();

        using var context = CreateContext();
        context.Users.Add(user);
        context.Watches.Add(new Watch
        {
            UserId = user.Id,
            SpaceId = null,
            PageId = null, // neither set - invalid
            CreatedAtUtc = DateTime.UtcNow,
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Watch_ExactlyOneSet_Saves_BothVariants()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Watches.Add(new Watch { UserId = user.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });
        context.Watches.Add(new Watch { UserId = user.Id, PageId = page.Id, CreatedAtUtc = DateTime.UtcNow });

        var exception = Record.Exception(() => context.SaveChanges());
        Assert.Null(exception);
    }
}
