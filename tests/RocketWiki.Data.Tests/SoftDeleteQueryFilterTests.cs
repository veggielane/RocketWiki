using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Space/Page/Attachment carry a global query filter hiding soft-deleted rows; Comment
/// deliberately does not (data-model.md: IsDeleted there is a tombstone that keeps thread
/// shape). Both halves of that asymmetry are worth proving, since a copy-paste mistake in
/// either direction would fail silently - "no filter" and "wrong filter" both compile fine.
/// </summary>
public class SoftDeleteQueryFilterTests : SqliteTestBase
{
    [Fact]
    public void SoftDeletedSpace_HiddenByDefault_VisibleWithIgnoreQueryFilters()
    {
        var space = TestData.NewSpace();
        space.IsDeleted = true;
        space.DeletedAtUtc = DateTime.UtcNow;
        space.DeletedByUserId = Guid.NewGuid();

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        Assert.Empty(readContext.Spaces.Where(s => s.Id == space.Id));
        Assert.Single(readContext.Spaces.IgnoreQueryFilters().Where(s => s.Id == space.Id));
    }

    [Fact]
    public void SoftDeletedPage_HiddenByDefault_VisibleWithIgnoreQueryFilters()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        page.IsDeleted = true;
        page.DeletedAtUtc = DateTime.UtcNow;

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        Assert.Empty(readContext.Pages.Where(p => p.Id == page.Id));
        Assert.Single(readContext.Pages.IgnoreQueryFilters().Where(p => p.Id == page.Id));
    }

    [Fact]
    public void SoftDeletedAttachment_HiddenByDefault_VisibleWithIgnoreQueryFilters()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var attachment = new Attachment
        {
            PageId = page.Id,
            FileName = "old.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1,
            ContentHash = new byte[32],
            StorageKey = "attachments/old",
            UploadedByUserId = user.Id,
            CreatedAtUtc = DateTime.UtcNow,
            IsDeleted = true,
            DeletedAtUtc = DateTime.UtcNow,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Users.Add(user);
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.Attachments.Add(attachment);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        Assert.Empty(readContext.Attachments.Where(a => a.Id == attachment.Id));
        Assert.Single(readContext.Attachments.IgnoreQueryFilters().Where(a => a.Id == attachment.Id));
    }

    [Fact]
    public void SoftDeletedComment_StillReturnedByDefault_TombstoneNotFiltered()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var comment = new Comment
        {
            PageId = page.Id,
            Body = string.Empty, // blanked by the application on delete
            AuthorUserId = user.Id,
            CreatedAtUtc = DateTime.UtcNow,
            IsDeleted = true,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Users.Add(user);
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.Comments.Add(comment);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();

        // No IgnoreQueryFilters() needed - Comment has no global filter to ignore.
        var reloaded = readContext.Comments.Single(c => c.Id == comment.Id);
        Assert.True(reloaded.IsDeleted);
    }
}
