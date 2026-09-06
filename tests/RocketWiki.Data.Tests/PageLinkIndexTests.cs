using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Tests.Content;
using RocketWiki.Data.Services;
using RocketWiki.Storage;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// data-model.md: the page link index follows <c>Page.CurrentContent</c> on every write
/// path that sets it — create, update, restore a revision, and a sync bundle's page
/// upsert — in the same unit of work. One test per path, each asserting the committed
/// rows are exactly <c>PageLink.FromContent</c> of the content that landed, because a
/// path that forgets the indexer does not fail: it leaves the graph quietly stale.
/// </summary>
public class PageLinkIndexTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");
    private static readonly Principal Editor = Principal.Create("editor-sub", []);

    private static AccessRule Grant(Guid spaceId, SpaceRole? role) => new()
    {
        Kind = role is null ? AccessRuleKind.AccessGrant : AccessRuleKind.RoleGrant,
        SpaceId = spaceId,
        Role = role,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static (Guid Target, int Ordinal)[] Expected(Guid sourceId, string content) =>
        PageLink.FromContent(sourceId, content).Select(l => (l.TargetPageId, l.Ordinal)).ToArray();

    private static (Guid Target, int Ordinal)[] Stored(RocketWikiDbContext context, Guid sourceId) =>
        context.PageLinks.AsNoTracking()
            .Where(l => l.SourcePageId == sourceId)
            .OrderBy(l => l.Ordinal)
            .Select(l => new { l.TargetPageId, l.Ordinal })
            .AsEnumerable()
            .Select(l => (l.TargetPageId, l.Ordinal))
            .ToArray();

    private static void SeedEditableSpace(RocketWikiDbContext context, Space space, User actor)
    {
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();
    }

    // --- The write paths -----------------------------------------------------------------

    [Fact]
    public async Task CreatePage_WritesTheIndexForTheNewContent()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        SeedEditableSpace(context, space, actor);

        var content = $"{PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.Dangling)} {PageLinkCorpus.Link(PageLinkCorpus.A)}";
        var created = await new PageService(context, LocalInstanceId).CreatePageAsync(
            new CreatePageRequest(space.Id, null, "a", "A", content), Editor, actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        using var verify = CreateContext();
        Assert.Equal(Expected(created.Value.Id, content), Stored(verify, created.Value.Id));
        Assert.Equal([(PageLinkCorpus.A, 0), (PageLinkCorpus.Dangling, 1)], Stored(verify, created.Value.Id));
    }

    [Fact]
    public async Task UpdatePageContent_ReplacesTheWholeSet_NotJustTheAdditions()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        SeedEditableSpace(context, space, actor);
        var service = new PageService(context, LocalInstanceId);

        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "a", "A", $"{PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.B)}"),
            Editor, actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        // A dropped, B moves from ordinal 1 to 0, Dangling appears: a survivor's ordinal is
        // updated, a removed link goes, and nothing from the old content lingers.
        var updated = $"{PageLinkCorpus.Link(PageLinkCorpus.B)} {PageLinkCorpus.Link(PageLinkCorpus.Dangling)}";
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(created.Value.Id, 1, "A", updated, null), Editor, actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        using var verify = CreateContext();
        Assert.Equal([(PageLinkCorpus.B, 0), (PageLinkCorpus.Dangling, 1)], Stored(verify, created.Value.Id));
        Assert.Equal(Expected(created.Value.Id, updated), Stored(verify, created.Value.Id));
    }

    [Fact]
    public async Task UpdatePageContent_ToNoLinks_EmptiesTheIndex()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        SeedEditableSpace(context, space, actor);
        var service = new PageService(context, LocalInstanceId);

        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "a", "A", PageLinkCorpus.Link(PageLinkCorpus.A)), Editor, actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);
        var result = await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(created.Value.Id, 1, "A", "# No links now", null), Editor, actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        using var verify = CreateContext();
        Assert.Empty(Stored(verify, created.Value.Id));
    }

    [Fact]
    public async Task RestoreRevision_ReindexesToTheRestoredContent()
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        SeedEditableSpace(context, space, actor);
        var service = new PageService(context, LocalInstanceId);

        var original = $"{PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.B)}";
        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "a", "A", original), Editor, actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);
        Assert.True((await service.UpdatePageContentAsync(
            new UpdatePageContentRequest(created.Value.Id, 1, "A", PageLinkCorpus.Link(PageLinkCorpus.Dangling), null),
            Editor, actor.Id, AuditCtx)).IsSuccess);

        var restored = await service.RestoreRevisionAsync(
            new RestoreRevisionRequest(created.Value.Id, RevisionNumberToRestore: 1, ExpectedCurrentRevisionNumber: 2),
            Editor, actor.Id, AuditCtx);
        Assert.True(restored.IsSuccess);

        using var verify = CreateContext();
        Assert.Equal(Expected(created.Value.Id, original), Stored(verify, created.Value.Id));
        Assert.Equal([(PageLinkCorpus.A, 0), (PageLinkCorpus.B, 1)], Stored(verify, created.Value.Id));
    }

    [Fact]
    public async Task IndexIsWrittenInTheSameUnitOfWorkAsTheContent_AFailedSaveLeavesNoRows()
    {
        // The rows ride in the same change set as the content, the revision and the audit
        // row (design.md §7). A save that fails - here, the missing AuditContext the
        // domain-event pipeline refuses - therefore commits none of it.
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        using var context = CreateContext();
        SeedEditableSpace(context, space, actor);
        var page = TestData.NewPage(space, "a");
        page.CurrentContent = PageLinkCorpus.Link(PageLinkCorpus.A);
        context.Pages.Add(page);
        await PageLinkIndex.ReplaceAsync(context, page, CancellationToken.None);
        context.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, actor.Id, page.Title));

        await Assert.ThrowsAsync<MissingAuditContextException>(() => context.SaveChangesAsync());

        using var verify = CreateContext();
        Assert.Empty(Stored(verify, page.Id));
        Assert.False(verify.Pages.Any(p => p.Id == page.Id));
    }

    // --- The bundle importer -------------------------------------------------------------

    /// <summary>
    /// design.md §12: the index never travels in a bundle; the replica rebuilds it from the
    /// content that did, on the initial upsert and again on every later one.
    /// </summary>
    [Fact]
    public async Task BundleImport_RebuildsTheIndexOnTheReplica_AndReplacesItOnALaterUpsert()
    {
        var actor = TestData.NewUser();
        var space = new Space
        {
            Key = "ENG",
            Name = "ENG Space",
            OriginInstanceId = LocalInstanceId,
            IsExported = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
        };
        using var lowContext = CreateContext();
        SeedEditableSpace(lowContext, space, actor);
        var pageService = new PageService(lowContext, LocalInstanceId);

        var first = $"{PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.B)}";
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", first), Editor, actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var storageDir = Path.Combine(Path.GetTempPath(), "rocketwiki-link-index-tests", Guid.NewGuid().ToString("N"));
        var outputDir = Path.Combine(Path.GetTempPath(), "rocketwiki-link-index-bundles", Guid.NewGuid().ToString("N"));
        var storage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
        {
            FileSystem = new FileSystemFileStorageOptions { Root = storageDir },
        }));
        try
        {
            var exportService = new BundleExportService(lowContext, storage);
            var bundle = await exportService.ExportIncrementalAsync(outputDir, LocalInstanceId);
            Assert.NotNull(bundle);

            using var highConnection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            highConnection.Open();
            using var highContext = new RocketWikiDbContext(
                new DbContextOptionsBuilder<RocketWikiDbContext>().UseSqlite(highConnection).Options);
            highContext.Database.EnsureCreated();

            var importService = new BundleImportService(highContext, storage);
            Assert.True((await importService.ImportAsync(bundle!.BundleFilePath, LocalInstanceId, AuditCtx)).IsSuccess);
            Assert.Equal(Expected(created.Value.Id, first), Stored(highContext, created.Value.Id));

            var second = $"{PageLinkCorpus.Link(PageLinkCorpus.B)} {PageLinkCorpus.Link(PageLinkCorpus.Dangling)}";
            Assert.True((await pageService.UpdatePageContentAsync(
                new UpdatePageContentRequest(created.Value.Id, 1, "Home", second, null), Editor, actor.Id, AuditCtx)).IsSuccess);
            var next = await exportService.ExportIncrementalAsync(outputDir, LocalInstanceId);
            Assert.NotNull(next);
            Assert.True((await importService.ImportAsync(next!.BundleFilePath, LocalInstanceId, AuditCtx)).IsSuccess);

            Assert.Equal(Expected(created.Value.Id, second), Stored(highContext, created.Value.Id));
            Assert.Equal([(PageLinkCorpus.B, 0), (PageLinkCorpus.Dangling, 1)], Stored(highContext, created.Value.Id));
        }
        finally
        {
            if (Directory.Exists(storageDir)) Directory.Delete(storageDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // --- The maintainer itself -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(CorpusCaseNames))]
    public async Task ReplaceAsync_WritesExactlyFromContent_ForEveryCorpusCase(string caseName)
    {
        // The incremental maintainer against the shared corpus - the same content the SQL
        // Server tier runs the migration's backfill over, so "backfill == incremental" is
        // pinned through one definition (PageLink.FromContent) on both sides.
        var testCase = PageLinkCorpus.Cases.Single(c => c.Name == caseName);
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "a");
        page.CurrentContent = testCase.Content;

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        await PageLinkIndex.ReplaceAsync(context, page, CancellationToken.None);
        await context.SaveChangesAsync();

        using var verify = CreateContext();
        var stored = Stored(verify, page.Id);
        Assert.Equal(Expected(page.Id, testCase.Content), stored);
        Assert.Equal(testCase.Expected.Select((id, i) => (id, i)), stored);
    }

    public static TheoryData<string> CorpusCaseNames { get; } = new(PageLinkCorpus.Cases.Select(c => c.Name));

    [Fact]
    public async Task ReplaceAsync_SamePageWrittenRepeatedlyInOneUnitOfWork_ReconcilesThroughTheTracker()
    {
        // A sync bundle applies every line before one SaveChanges, so the same page can be
        // upserted several times unsaved. Rows added by an earlier pass exist only in the
        // tracker; rows deleted by one may be wanted again by the next.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "a");
        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);

        page.CurrentContent = $"{PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.B)}";
        await PageLinkIndex.ReplaceAsync(context, page, CancellationToken.None);
        page.CurrentContent = $"{PageLinkCorpus.Link(PageLinkCorpus.B)} {PageLinkCorpus.Link(PageLinkCorpus.Dangling)}";
        await PageLinkIndex.ReplaceAsync(context, page, CancellationToken.None); // A never reaches the database
        await context.SaveChangesAsync();
        Assert.Equal([(PageLinkCorpus.B, 0), (PageLinkCorpus.Dangling, 1)], Stored(context, page.Id));

        // Now against persisted rows: drop B, then want it back before saving.
        page.CurrentContent = PageLinkCorpus.Link(PageLinkCorpus.Dangling);
        await PageLinkIndex.ReplaceAsync(context, page, CancellationToken.None);
        page.CurrentContent = $"{PageLinkCorpus.Link(PageLinkCorpus.Dangling)} {PageLinkCorpus.Link(PageLinkCorpus.B)}";
        await PageLinkIndex.ReplaceAsync(context, page, CancellationToken.None);
        await context.SaveChangesAsync();

        using var verify = CreateContext();
        Assert.Equal([(PageLinkCorpus.Dangling, 0), (PageLinkCorpus.B, 1)], Stored(verify, page.Id));
    }

    // --- The schema --------------------------------------------------------------------

    [Fact]
    public async Task ADanglingTarget_IsStorable_ThereIsNoForeignKeyOnTheTarget()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "a");
        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageLinks.Add(new PageLink { SourcePageId = page.Id, TargetPageId = Guid.NewGuid(), Ordinal = 0 });

        var exception = await Record.ExceptionAsync(() => context.SaveChangesAsync());

        Assert.Null(exception);
    }

    [Fact]
    public void DeletingAPage_TakesItsIndexRowsWithIt_TheOneCascadeInTheSchema()
    {
        // Same stub-delete technique as NoCascadeDeleteTests, for the opposite assertion:
        // a page row with only index rows hanging off it (no revisions - the product never
        // hard-deletes a page at all) deletes cleanly, and the rows go with it.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "a");
        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageLinks.Add(new PageLink { SourcePageId = page.Id, TargetPageId = PageLinkCorpus.A, Ordinal = 0 });
            writeContext.SaveChanges();
        }

        using (var deleteContext = CreateContext())
        {
            deleteContext.PageMarkings.Remove(new PageMarking { PageId = page.Id });
            deleteContext.Pages.Remove(new Page { Id = page.Id });
            deleteContext.SaveChanges();
        }

        using var verify = CreateContext();
        Assert.False(verify.Pages.IgnoreQueryFilters().Any(p => p.Id == page.Id));
        Assert.Empty(verify.PageLinks.Where(l => l.SourcePageId == page.Id));
    }
}
