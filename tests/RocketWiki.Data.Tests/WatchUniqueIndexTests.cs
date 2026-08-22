using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Proves the fix for the Watch index bug found while hand-reviewing the migration: a
/// single unique (UserId, SpaceId, PageId) index would have been silently useless (EF's
/// nullable-column convention would AND-filter on both being non-null, which can never
/// be true given CK_Watches_SpaceXorPage). Replaced with two filtered unique indexes -
/// these tests confirm both that real duplicates are now rejected and that the case the
/// broken version would have mishandled (a user watching both a space and a page at the
/// same time, or watching several distinct targets) still works.
/// </summary>
public class WatchUniqueIndexTests : SqliteTestBase
{
    [Fact]
    public void DuplicateSpaceWatch_SameUserSameSpace_IsRejected()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Watches.Add(new Watch { UserId = user.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });
        context.SaveChanges();

        context.Watches.Add(new Watch { UserId = user.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void DuplicatePageWatch_SameUserSamePage_IsRejected()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.Watches.Add(new Watch { UserId = user.Id, PageId = page.Id, CreatedAtUtc = DateTime.UtcNow });
        context.SaveChanges();

        context.Watches.Add(new Watch { UserId = user.Id, PageId = page.Id, CreatedAtUtc = DateTime.UtcNow });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void SameUser_WatchingBothASpaceAndAPage_BothSucceed()
    {
        // The legitimate case the broken composite index would have mishandled: this is
        // not a "duplicate" by any reasonable definition, and must not collide just
        // because both rows share the same UserId.
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
        Assert.Equal(2, context.Watches.Count());
    }

    [Fact]
    public void SameUser_WatchingTwoDifferentSpaces_BothSucceed()
    {
        var user = TestData.NewUser();
        var spaceA = TestData.NewSpace("ENG");
        var spaceB = TestData.NewSpace("OPS");

        using var context = CreateContext();
        context.Users.Add(user);
        context.Spaces.AddRange(spaceA, spaceB);
        context.Watches.Add(new Watch { UserId = user.Id, SpaceId = spaceA.Id, CreatedAtUtc = DateTime.UtcNow });
        context.Watches.Add(new Watch { UserId = user.Id, SpaceId = spaceB.Id, CreatedAtUtc = DateTime.UtcNow });

        var exception = Record.Exception(() => context.SaveChanges());
        Assert.Null(exception);
    }

    [Fact]
    public void SameSpace_WatchedByTwoDifferentUsers_BothSucceed()
    {
        // Uniqueness is scoped per-user, not global.
        var userA = TestData.NewUser("A");
        var userB = TestData.NewUser("B");
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.AddRange(userA, userB);
        context.Spaces.Add(space);
        context.Watches.Add(new Watch { UserId = userA.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });
        context.Watches.Add(new Watch { UserId = userB.Id, SpaceId = space.Id, CreatedAtUtc = DateTime.UtcNow });

        var exception = Record.Exception(() => context.SaveChanges());
        Assert.Null(exception);
    }
}
