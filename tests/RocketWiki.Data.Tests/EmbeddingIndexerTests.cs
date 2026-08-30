using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RocketWiki.Core.Search;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §9.2 (milestone 7): the embedding background job's engine, through the real
/// pipeline on SQLite with a deterministic fake endpoint. Covers the trigger (revision
/// scan), the chunk-hash skip ("only changed chunks re-embed"), failure isolation (an
/// unreachable endpoint marks-for-retry and never corrupts the store), backoff, and the
/// trash purge / restore re-embed cycle.
///
/// Each indexer run gets a FRESH DbContext, exactly like production (the background
/// service opens a scope per run) — reusing one context across runs would mix
/// ExecuteDelete with stale tracked entities in a way no real caller does.
/// </summary>
public class EmbeddingIndexerTests : SqliteTestBase
{
    private const int Dimensions = 8;

    private int _spaceKeySequence;

    /// <summary>Two sections, each large enough (about 2200 chars) that the default chunker options keep them as separate chunks.</summary>
    private static string TwoSectionContent(string sectionOneWord, string sectionTwoWord) =>
        $"# Alpha\n\n{Filler(sectionOneWord)}\n\n# Beta\n\n{Filler(sectionTwoWord)}\n";

    private static string Filler(string word) =>
        string.Join(" ", Enumerable.Repeat($"{word} lorem ipsum dolor sit amet", 80));

    private static FakeEmbeddingGenerator NewGenerator() =>
        new(text => Enumerable.Range(0, Dimensions).Select(i => (float)((text.Length % (i + 2)) + 1)).ToArray());

    private static EmbeddingOptions Options(
        TimeSpan? failureBackoff = null, int maxAttempts = 5, int abortAfterConsecutiveFailures = 3) =>
        new("fake-model", Dimensions, BatchSize: 16, FailureBackoff: failureBackoff ?? TimeSpan.Zero,
            MaxAttempts: maxAttempts, AbortAfterConsecutiveFailures: abortAfterConsecutiveFailures);

    private async Task<EmbeddingIndexRun> RunIndexerAsync(FakeEmbeddingGenerator generator, EmbeddingOptions? options = null)
    {
        using var db = CreateContext();
        var indexer = new EmbeddingIndexer(db, generator, options ?? Options(), NullLogger<EmbeddingIndexer>.Instance);
        return await indexer.RunOnceAsync();
    }

    private Guid SeedPage(string content, int revisionNumber = 1, DateTime? updatedAt = null)
    {
        using var db = CreateContext();
        // A space per page, with a distinct key: Space.Key is unique and TestData's
        // default is a constant, so a test that seeds more than one page needs its own.
        var space = TestData.NewSpace($"ENG{Interlocked.Increment(ref _spaceKeySequence)}");
        var page = TestData.NewPage(space, "engines");
        page.CurrentContent = content;
        page.CurrentRevisionNumber = revisionNumber;
        if (updatedAt is not null)
        {
            // The scan orders oldest-first, so which page a batch reaches FIRST is the
            // whole point of the starvation tests below — it cannot be left to how fast
            // two seeds run.
            page.UpdatedAtUtc = updatedAt.Value;
        }

        db.Spaces.Add(space);
        db.Pages.Add(page);
        db.SaveChanges();
        return page.Id;
    }

    private void UpdatePage(Guid pageId, Action<Core.Entities.Page> mutate)
    {
        using var db = CreateContext();
        var page = db.Pages.IgnoreQueryFilters().Single(p => p.Id == pageId);
        mutate(page);
        page.UpdatedAtUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    [Fact]
    public async Task NewPage_EmbedsAllChunks_AndRecordsState()
    {
        var pageId = SeedPage(TwoSectionContent("turbine", "nozzle"));

        var generator = NewGenerator();
        var run = await RunIndexerAsync(generator);

        Assert.Equal(1, run.PagesPending);
        Assert.Equal(1, run.PagesEmbedded);
        Assert.Equal(0, run.PagesFailed);
        Assert.Equal(2, run.ChunksEmbedded);
        Assert.False(run.Aborted);

        using var db = CreateContext();
        var rows = db.PageEmbeddings.Where(e => e.PageId == pageId).OrderBy(e => e.ChunkIndex).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Alpha", rows[0].HeadingPath);
        Assert.Equal("Beta", rows[1].HeadingPath);
        Assert.All(rows, r => Assert.Equal("fake-model", r.Model));
        Assert.All(rows, r => Assert.Equal(Dimensions, r.Embedding.Length));
        Assert.All(rows, r => Assert.Equal(32, r.ChunkHash.Length));

        var state = db.PageEmbeddingStates.Single(s => s.PageId == pageId);
        Assert.Equal(1, state.EmbeddedRevisionNumber);
        Assert.Equal(0, state.FailedAttempts);

        // The breadcrumb-prefixed embedding inputs are what went to the "endpoint".
        Assert.Equal(2, generator.Inputs.Count);
        Assert.StartsWith("Alpha\n\n# Alpha", generator.Inputs[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_ReembedsOnlyTheChangedChunk_ExactlyOnce()
    {
        var pageId = SeedPage(TwoSectionContent("turbine", "nozzle"));
        var generator = NewGenerator();
        await RunIndexerAsync(generator);
        Assert.Equal(2, generator.Inputs.Count);

        // Edit section Beta only; a real mutation bumps the revision number.
        UpdatePage(pageId, p =>
        {
            p.CurrentContent = TwoSectionContent("turbine", "aerospike");
            p.CurrentRevisionNumber = 2;
        });

        var run = await RunIndexerAsync(generator);

        // §9.2 "chunks are content-hashed so only changed chunks re-embed":
        // one page due, ONE chunk re-embedded, not two.
        Assert.Equal(1, run.PagesEmbedded);
        Assert.Equal(1, run.ChunksEmbedded);
        Assert.Equal(3, generator.Inputs.Count);
        Assert.Contains("aerospike", generator.Inputs[2], StringComparison.Ordinal);

        using (var db = CreateContext())
        {
            Assert.Equal(2, db.PageEmbeddingStates.Single(s => s.PageId == pageId).EmbeddedRevisionNumber);
        }

        // And exactly once: an immediately following run finds nothing due.
        var idleRun = await RunIndexerAsync(generator);
        Assert.Equal(0, idleRun.PagesPending);
        Assert.Equal(0, idleRun.PagesEmbedded);
        Assert.Equal(3, generator.Inputs.Count);
    }

    [Fact]
    public async Task PageShrinks_StaleTrailingChunksAreRemoved()
    {
        var pageId = SeedPage(TwoSectionContent("turbine", "nozzle"));
        var generator = NewGenerator();
        await RunIndexerAsync(generator);

        UpdatePage(pageId, p =>
        {
            p.CurrentContent = $"# Alpha\n\n{Filler("turbine")}\n";
            p.CurrentRevisionNumber = 2;
        });
        await RunIndexerAsync(generator);

        using var db = CreateContext();
        var rows = db.PageEmbeddings.Where(e => e.PageId == pageId).ToList();
        Assert.Single(rows);
        Assert.Equal(0, rows[0].ChunkIndex);
    }

    [Fact]
    public async Task EndpointDown_MarksForRetry_StoresNothing_AndLaterRecovers()
    {
        var pageId = SeedPage(TwoSectionContent("turbine", "nozzle"));

        var generator = NewGenerator();
        generator.ThrowOnGenerate = new HttpRequestException("connection refused");

        var failedRun = await RunIndexerAsync(generator);

        Assert.Equal(1, failedRun.PagesFailed);
        Assert.Equal(0, failedRun.PagesEmbedded);
        // NOT aborted: one due page, so nothing was left to abandon. Aborted now means
        // "due pages were deliberately skipped because the endpoint looked down", which
        // takes a run of consecutive failures to establish — see
        // RunOnce_WhenEveryPageFailsInARow_StopsInsteadOfWalkingTheWholeBatch. A single
        // failure can equally mean the endpoint is up and this one page is unembeddable,
        // and treating those alike is what let one page starve the queue.
        Assert.False(failedRun.Aborted);

        using (var db = CreateContext())
        {
            Assert.Empty(db.PageEmbeddings.Where(e => e.PageId == pageId).ToList());
            var state = db.PageEmbeddingStates.Single(s => s.PageId == pageId);
            Assert.Equal(1, state.FailedAttempts);
            Assert.Equal(1, state.FailedRevisionNumber);
            Assert.Equal(0, state.EmbeddedRevisionNumber);

            // The page row itself was never touched - saves don't depend on embeddings.
            Assert.Equal(1, db.Pages.Single(p => p.Id == pageId).CurrentRevisionNumber);
        }

        // Endpoint comes back (zero backoff in these options): next run succeeds and clears the failure.
        generator.ThrowOnGenerate = null;
        var recoveredRun = await RunIndexerAsync(generator);
        Assert.Equal(1, recoveredRun.PagesEmbedded);

        using (var db = CreateContext())
        {
            Assert.Equal(2, db.PageEmbeddings.Count(e => e.PageId == pageId));
            var state = db.PageEmbeddingStates.Single(s => s.PageId == pageId);
            Assert.Equal(0, state.FailedAttempts);
            Assert.Equal(1, state.EmbeddedRevisionNumber);
        }
    }

    [Fact]
    public async Task FailedPage_WaitsOutTheBackoff_BeforeRetrying()
    {
        SeedPage("# Solo\n\nsmall page");

        var generator = NewGenerator();
        generator.ThrowOnGenerate = new HttpRequestException("down");
        var backoff = Options(failureBackoff: TimeSpan.FromHours(1));
        await RunIndexerAsync(generator, backoff);
        generator.ThrowOnGenerate = null;

        var backedOffRun = await RunIndexerAsync(generator, backoff);

        // Still pending - honestly reported - but not attempted inside the backoff window.
        Assert.Equal(1, backedOffRun.PagesPending);
        Assert.Equal(0, backedOffRun.PagesEmbedded);
        Assert.Empty(generator.Inputs);
    }

    [Fact]
    public async Task TrashedPage_IsPurged_AndReembedsOnRestore()
    {
        var pageId = SeedPage(TwoSectionContent("turbine", "nozzle"));
        var generator = NewGenerator();
        await RunIndexerAsync(generator);

        using (var db = CreateContext())
        {
            Assert.Equal(2, db.PageEmbeddings.Count(e => e.PageId == pageId));
        }

        UpdatePage(pageId, p =>
        {
            p.IsDeleted = true;
            p.DeletedAtUtc = DateTime.UtcNow;
        });
        await RunIndexerAsync(generator);

        // Trash leaves no vectors behind - a trashed page's content must not linger in
        // a store the search scan reads.
        using (var db = CreateContext())
        {
            Assert.Equal(0, db.PageEmbeddings.Count(e => e.PageId == pageId));
            Assert.Empty(db.PageEmbeddingStates.Where(s => s.PageId == pageId).ToList());
        }

        UpdatePage(pageId, p =>
        {
            p.IsDeleted = false;
            p.DeletedAtUtc = null;
        });
        var restoreRun = await RunIndexerAsync(generator);

        // The purged state row is exactly what makes a restored page due again.
        Assert.Equal(1, restoreRun.PagesEmbedded);
        using (var db = CreateContext())
        {
            Assert.Equal(2, db.PageEmbeddings.Count(e => e.PageId == pageId));
        }
    }

    // ---- Poison-page starvation (design.md §9.2) -----------------------------------
    //
    // The scan is oldest-first and a page stays due until it succeeds, so a page the
    // endpoint can never embed is at the head of every batch forever. Combined with a
    // batch that gave up at its first failure, one such page meant NO page was ever
    // embedded again — semantic search silently frozen at the moment that page was
    // written, with nothing in the run record saying so. The three mechanisms below are
    // tested separately because each one alone leaves the starvation intact.

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Content the fake endpoint rejects, in both chunks, however it is chunked.</summary>
    private static string PoisonContent() => TwoSectionContent("cyanide", "cyanide");

    [Fact]
    public async Task RunOnce_WhenTheOldestPageAlwaysFails_StillEmbedsThePagesBehindIt()
    {
        var poisonId = SeedPage(PoisonContent(), updatedAt: Epoch);
        var goodId = SeedPage(TwoSectionContent("turbine", "nozzle"), updatedAt: Epoch.AddMinutes(1));

        var generator = NewGenerator();
        generator.PoisonMarker = "cyanide";
        var run = await RunIndexerAsync(generator);

        Assert.Equal(1, run.PagesFailed);
        // The page behind the poison one is the assertion: a failure is a page's problem,
        // not the batch's.
        Assert.Equal(1, run.PagesEmbedded);
        // One failure with successes around it is not evidence the endpoint is down.
        Assert.False(run.Aborted);

        using var db = CreateContext();
        Assert.Equal(1, db.PageEmbeddingStates.Single(s => s.PageId == goodId).EmbeddedRevisionNumber);
        Assert.Equal(0, db.PageEmbeddingStates.Single(s => s.PageId == poisonId).EmbeddedRevisionNumber);
    }

    [Fact]
    public async Task RunOnce_AfterMaxAttempts_QuarantinesTheRevisionAndStopsAttemptingIt()
    {
        var poisonId = SeedPage(PoisonContent(), updatedAt: Epoch);
        var options = Options(maxAttempts: 2);

        var generator = NewGenerator();
        generator.PoisonMarker = "cyanide";

        var first = await RunIndexerAsync(generator, options);
        Assert.Equal(1, first.PagesFailed);
        Assert.Equal(0, first.PagesQuarantined);

        var second = await RunIndexerAsync(generator, options);
        Assert.Equal(1, second.PagesFailed);
        // The attempt that exhausts the ceiling reports the quarantine in the same run.
        Assert.Equal(1, second.PagesQuarantined);

        var attemptsBefore = generator.Attempts;
        var third = await RunIndexerAsync(generator, options);

        // Not attempted at all — not merely failing more cheaply. Retrying forever is
        // what starves the queue; the count is what tells an operator the page is gone
        // from semantic search.
        Assert.Equal(attemptsBefore, generator.Attempts);
        Assert.Equal(0, third.PagesFailed);
        Assert.Equal(1, third.PagesQuarantined);
        // Still counted as pending: quarantine hides a page from the batch, it does not
        // pretend the page is embedded.
        Assert.Equal(1, third.PagesPending);

        using var db = CreateContext();
        var state = db.PageEmbeddingStates.Single(s => s.PageId == poisonId);
        Assert.Equal(2, state.FailedAttempts);
        Assert.Equal(1, state.FailedRevisionNumber);
    }

    [Fact]
    public async Task RunOnce_WhenAQuarantinedPageIsEdited_AttemptsItAgain()
    {
        var pageId = SeedPage(PoisonContent(), updatedAt: Epoch);
        var options = Options(maxAttempts: 2);

        var generator = NewGenerator();
        generator.PoisonMarker = "cyanide";
        await RunIndexerAsync(generator, options);
        await RunIndexerAsync(generator, options);
        Assert.Equal(1, (await RunIndexerAsync(generator, options)).PagesQuarantined);

        // Editing the page is the only cure available to a wiki user, so the quarantine
        // predicate is matched against the CURRENT revision: a quarantine that outlived
        // the content it was imposed on would keep the fixed page out of search forever.
        UpdatePage(pageId, p =>
        {
            p.CurrentContent = TwoSectionContent("turbine", "nozzle");
            p.CurrentRevisionNumber = 2;
        });

        var run = await RunIndexerAsync(generator, options);

        Assert.Equal(1, run.PagesEmbedded);
        Assert.Equal(0, run.PagesQuarantined);

        using var db = CreateContext();
        var state = db.PageEmbeddingStates.Single(s => s.PageId == pageId);
        Assert.Equal(2, state.EmbeddedRevisionNumber);
        Assert.Equal(0, state.FailedAttempts);
        Assert.Equal(0, state.FailedRevisionNumber);
    }

    [Fact]
    public async Task RunOnce_WhenAnEditedPageStillFails_GivesTheNewRevisionAFullAttemptBudget()
    {
        var pageId = SeedPage(PoisonContent(), updatedAt: Epoch);
        var options = Options(maxAttempts: 2);

        var generator = NewGenerator();
        generator.PoisonMarker = "cyanide";
        await RunIndexerAsync(generator, options);
        await RunIndexerAsync(generator, options);

        // Edited, but the author did not fix the thing the endpoint objects to. The
        // failures already recorded were counted against content this page no longer
        // has, so they must not be spent against the new revision — otherwise every
        // edit after the first quarantine gets exactly one attempt, and an author
        // fixing the page by trial and error is told nothing and gets nowhere.
        UpdatePage(pageId, p => p.CurrentRevisionNumber = 2);

        var afterEdit = await RunIndexerAsync(generator, options);
        Assert.Equal(1, afterEdit.PagesFailed);
        Assert.Equal(0, afterEdit.PagesQuarantined);

        using (var db = CreateContext())
        {
            var state = db.PageEmbeddingStates.Single(s => s.PageId == pageId);
            Assert.Equal(1, state.FailedAttempts);
            Assert.Equal(2, state.FailedRevisionNumber);
        }

        // And the budget is a budget, not an amnesty: the new revision quarantines on
        // its own second failure.
        Assert.Equal(1, (await RunIndexerAsync(generator, options)).PagesQuarantined);
    }

    [Fact]
    public async Task RunOnce_WhenEveryPageFailsInARow_StopsInsteadOfWalkingTheWholeBatch()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedPage(TwoSectionContent("turbine", "nozzle"), updatedAt: Epoch.AddMinutes(i));
        }

        var generator = NewGenerator();
        generator.ThrowOnGenerate = new HttpRequestException("endpoint unreachable");

        var run = await RunIndexerAsync(generator, Options(abortAfterConsecutiveFailures: 3));

        // The original protection, kept: one endpoint serves every page, so failures with
        // no success between them mean the endpoint is down and the rest of the batch
        // would only stack failure counts onto innocent revisions.
        Assert.True(run.Aborted);
        Assert.Equal(3, run.PagesFailed);
        Assert.Equal(3, generator.Attempts);
        Assert.Equal(0, run.PagesQuarantined);
    }
}
