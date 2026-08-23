using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Data.Tests;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// Closes the README/design.md §16 unverified item: "Full-text search ... SQL Server
/// features the SQLite tier cannot exercise - they remain TODO-flagged in the
/// migration and unproven" (milestone 4's "SQL Server FTS path TODO-flagged until the
/// container tier exists"). SearchService picks its keyword branch by provider name;
/// on this tier that is the CONTAINSTABLE branch against the migration's real
/// FULLTEXT index. Every match asserted here is a *stemming-only* match - the query
/// word never appears literally in any page - which the LIKE fallback can never
/// produce, so a hit is positive proof the FTS branch executed, not a lookalike.
///
/// Pages are seeded through the real service layer (PageService), the same write path
/// production uses, so CHANGE_TRACKING AUTO picks the rows up exactly as it would in
/// production. FTS population is asynchronous; tests poll with a hard deadline
/// instead of assuming immediacy.
/// </summary>
public sealed class FullTextSearchTests : SqlServerTestBase
{
    private static readonly TimeSpan PopulationDeadline = TimeSpan.FromSeconds(90);
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-fts", "127.0.0.1");

    public FullTextSearchTests(SqlServerContainerFixture fixture)
        : base(fixture)
    {
    }

    private static Principal ViewerPrincipal(params string[] groups) => Principal.Create("viewer-sub", groups);

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule ViewRestriction(Guid pageId) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = """{ "group": "top-secret" }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    [SqlServerFact]
    public async Task Search_StemsTheQuery_AndRanksByFtsRelevance_ProvingTheContainstableBranchRan()
    {
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, DefaultLocalInstanceId);

        // Neither page ever contains the literal word "running" - only inflections.
        // LIKE '%running%' finds nothing here; only word-breaker stemming can.
        var strong = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "pump-runs",
                "Run the pumps",
                "# Run procedure\n\nRun the pump. Run it twice. Every run is logged; runs are reviewed."),
            ViewerPrincipal(), user.Id, AuditCtx);
        Assert.True(strong.IsSuccess);

        var weak = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "ops-handbook",
                "Operations handbook",
                "# Handbook\n\n" + string.Join(' ', Enumerable.Repeat("filler", 300))
                    + " one of the pumps runs nightly " + string.Join(' ', Enumerable.Repeat("filler", 300))),
            ViewerPrincipal(), user.Id, AuditCtx);
        Assert.True(weak.IsSuccess);

        var searchService = new SearchService(context);
        var hits = await SearchUntilAsync(
            searchService, new SearchRequest("running", null, null), ViewerPrincipal(),
            maxResults: 10, ready: h => h.Count >= 2);

        Assert.Equal(2, hits.Count);
        // Ranking sanity: CONTAINSTABLE RANK orders the term-dense short page above the
        // single buried mention. The LIKE fallback would have ordered by recency -
        // which would put `weak` (created later) first - so this order is itself
        // evidence the FTS branch produced the candidates.
        Assert.Equal(strong.Value.Id, hits[0].PageId);
        Assert.Equal(weak.Value.Id, hits[1].PageId);
    }

    [SqlServerFact]
    public async Task Search_OnTheFtsBranch_StillDropsRestrictedPages_AbsentNotForbidden()
    {
        // The adversarial permission test mirrored from SearchServiceTests
        // (Search_RestrictedMatch_IsAbsentFromResults / _NeverSurfacesInAnyReturnedField),
        // re-proven on the CONTAINSTABLE branch: over-fetch-then-canView (design.md
        // §9.3) must hold no matter which provider produced the candidates.
        const string secretPhrase = "the-classified-payload-manifest";

        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, DefaultLocalInstanceId);

        var visible = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "checklist", "Public checklist", "# Public\n\nRun the checklist."),
            ViewerPrincipal(), user.Id, AuditCtx);
        Assert.True(visible.IsSuccess);

        var restricted = await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "restricted", $"Secret {secretPhrase}",
                $"# Secret\n\nRun the transfer of {secretPhrase}."),
            ViewerPrincipal(), user.Id, AuditCtx);
        Assert.True(restricted.IsSuccess);

        context.AccessRules.Add(ViewRestriction(restricted.Value.Id));
        context.SaveChanges();

        // Airtight setup: wait until the raw FTS index itself returns BOTH pages for
        // the stemmed query. From that moment, the restricted page being missing from
        // the service's results can only be the permission filter - not an index that
        // simply hadn't populated it yet.
        await WaitForRawFtsHitsAsync(context, [visible.Value.Id, restricted.Value.Id]);

        var searchService = new SearchService(context);
        var hits = await searchService.SearchAsync(
            new SearchRequest("running", null, null), ViewerPrincipal(), maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(visible.Value.Id, hit.PageId);

        // §6.7: nothing returned may carry the restricted page's text either.
        var everyReturnedString = string.Join('\n',
            hits.SelectMany(h => new[] { h.Title, h.Snippet, h.AnchorId }.Concat(h.HeadingPath)));
        Assert.DoesNotContain(secretPhrase, everyReturnedString, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<SearchHit>> SearchUntilAsync(
        SearchService service, SearchRequest request, Principal principal, int maxResults,
        Func<IReadOnlyList<SearchHit>, bool> ready)
    {
        var deadline = DateTime.UtcNow + PopulationDeadline;
        IReadOnlyList<SearchHit> hits = [];
        while (DateTime.UtcNow < deadline)
        {
            hits = await service.SearchAsync(request, principal, maxResults);
            if (ready(hits))
            {
                return hits;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        // Deadline passed: return the last observation and let the caller's asserts
        // fail with the actual (informative) shape rather than a bare timeout.
        return hits;
    }

    private static async Task WaitForRawFtsHitsAsync(RocketWikiDbContext context, Guid[] expectedIds)
    {
        var condition = FullTextQueryBuilder.BuildContainsCondition("running");
        var deadline = DateTime.UtcNow + PopulationDeadline;
        List<Guid> found = [];
        while (DateTime.UtcNow < deadline)
        {
            found = await context.Database.SqlQuery<Guid>(
                    $"SELECT [KEY] AS [Value] FROM CONTAINSTABLE(Pages, (Title, CurrentContent), {condition})")
                .ToListAsync();
            if (expectedIds.All(found.Contains))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        Assert.Fail(
            $"FTS population did not index the expected pages within {PopulationDeadline.TotalSeconds}s. " +
            $"Expected {string.Join(", ", expectedIds)}; raw CONTAINSTABLE returned [{string.Join(", ", found)}].");
    }
}
