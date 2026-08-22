using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// data-model.md: no cascade deletes anywhere - every FK is ON DELETE NO ACTION, so
/// deleting a row a child still references must fail loudly rather than silently taking
/// the children with it. SQLite only enforces this because SqliteTestBase turns on
/// `PRAGMA foreign_keys`; without that pragma these would pass for the wrong reason.
///
/// Each delete below is issued from a fresh, untracked context via a key-only stub
/// entity, not the context that just created the parent/child graph. That matters: if
/// the child were still loaded in the same change tracker, EF Core's own client-side
/// "severed relationship" guard would throw an InvalidOperationException before any SQL
/// is even sent - a real error, but the wrong one. It would prove EF's in-memory
/// fixup logic works, not that the database's own FK constraint (the thing
/// data-model.md actually specifies) rejects the delete. Deleting via a stub forces a
/// bare DELETE statement to reach SQLite and be rejected there.
/// </summary>
public class NoCascadeDeleteTests : SqliteTestBase
{
    [Fact]
    public void DeletingSpace_WithExistingPage_ThrowsRatherThanCascading()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.SaveChanges();
        }

        using (var deleteContext = CreateContext())
        {
            deleteContext.Spaces.Remove(new Space { Id = space.Id });
            Assert.ThrowsAny<DbUpdateException>(() => deleteContext.SaveChanges());
        }

        using var verifyContext = CreateContext();
        Assert.True(verifyContext.Spaces.Any(s => s.Id == space.Id));
        Assert.True(verifyContext.Pages.Any(p => p.Id == page.Id));
    }

    [Fact]
    public void DeletingPage_WithExistingRevision_ThrowsRatherThanCascading()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var revision = TestData.NewRevision(page, user);

        using (var writeContext = CreateContext())
        {
            writeContext.Users.Add(user);
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageRevisions.Add(revision);
            writeContext.SaveChanges();
        }

        using var deleteContext = CreateContext();
        deleteContext.Pages.Remove(new Page { Id = page.Id });
        Assert.ThrowsAny<DbUpdateException>(() => deleteContext.SaveChanges());
    }

    [Fact]
    public void DeletingUser_WhoAuthoredARevision_ThrowsRatherThanCascading()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var revision = TestData.NewRevision(page, user);

        using (var writeContext = CreateContext())
        {
            writeContext.Users.Add(user);
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageRevisions.Add(revision);
            writeContext.SaveChanges();
        }

        using var deleteContext = CreateContext();
        deleteContext.Users.Remove(new User { Id = user.Id });
        Assert.ThrowsAny<DbUpdateException>(() => deleteContext.SaveChanges());
    }

    [Fact]
    public void DeletingSpace_WithNoChildren_Succeeds()
    {
        // Control case: proves the failures above are genuinely about the FK, not some
        // unrelated SaveChanges problem with deleting a Space at all.
        var space = TestData.NewSpace();

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.SaveChanges();
        }

        using var deleteContext = CreateContext();
        deleteContext.Spaces.Remove(new Space { Id = space.Id });
        var exception = Record.Exception(() => deleteContext.SaveChanges());

        Assert.Null(exception);
    }
}
