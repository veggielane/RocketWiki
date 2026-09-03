using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Tests.Access;
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
    public void AccessRule_AccessGrantWithPageIdInsteadOfSpaceId_ViolatesKindColumnPairing()
    {
        using var context = CreateContext();
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            PageId = Guid.NewGuid(), // wrong column for this Kind
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AccessRule_RoleGrantMissingRole_ViolatesKindColumnPairing()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = null, // required on a role grant
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
    public void AccessRule_ValidAccessGrant_Saves()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
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
    public void AccessRule_RoleGrantWithViewerRole_ViolatesKindColumnPairing()
    {
        // "Viewer" is retired (design.md §6.4): the value 1 is refused on a role grant by
        // the constraint itself, so the split migration's conversion cannot be undone one
        // row at a time by a stray write, and no code path can resurrect the old role.
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = (SpaceRole)1,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AccessRule_AccessGrantWithRole_ViolatesKindColumnPairing()
    {
        // An access grant confers visibility and nothing else; a role on it would be a
        // second, unaudited way to hold a role.
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AccessRule_ValidRoleGrant_Saves()
    {
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });

        Assert.Null(Record.Exception(() => context.SaveChanges()));
    }

    [Fact]
    public void AccessRuleSelector_RowsSaveAgainstAnAccessGrant_AndTwoValuesInOneCategoryAreAllowed()
    {
        // The grant-side PK includes the value (design.md §21.15): a grant may confer
        // APPLE and BANANA both, unlike a page's marking.
        var space = TestData.NewSpace();
        var rule = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        };
        rule.Selectors.Add(new AccessRuleSelector { AccessRuleId = rule.Id, Category = "FRUIT", Value = "APPLE" });
        rule.Selectors.Add(new AccessRuleSelector { AccessRuleId = rule.Id, Category = "FRUIT", Value = "BANANA" });

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.Add(rule);

        Assert.Null(Record.Exception(() => context.SaveChanges()));
        Assert.Equal(2, context.AccessRuleSelectors.Count(s => s.AccessRuleId == rule.Id));
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
    public void PageMarkingSelector_TwoValuesForOneCategory_ViolatesPrimaryKey()
    {
        // design.md §21.15: "at most one value per category on a page" is the PRIMARY KEY
        // (PageId, Category), not application discipline - a second value for FRUIT is a
        // key violation whatever code path tried to write it.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Official).WithSelectors(TestCatalogs.Apple));
        context.SaveChanges();

        // A second context, so EF's own identity map is not what refuses the row.
        using var second = CreateContext();
        second.PageMarkingSelectors.Add(new PageMarkingSelector { PageId = page.Id, Category = "FRUIT", Value = "BANANA" });

        Assert.ThrowsAny<DbUpdateException>(() => second.SaveChanges());
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
