using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Core.Tests.Content;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §6.7 / §21.8: the document graph is an OMITTING surface. A page the caller
/// cannot view is not a node, not an endpoint of any edge, and not a unit in any count —
/// whether it is hidden by a restriction, by a space they hold no access grant in, by a
/// marking gate, by the trash, or by an archived space. The fixture below links every
/// kind of hidden page both to and from a visible one, and the tests assert that none of
/// it shows; a second, privileged principal proves the same fixture does show it when the
/// gate passes, so a filter that silently stopped filtering could not pass by accident.
/// </summary>
public class PageGraphServiceTests : SqliteTestBase
{
    private static Principal Plain() => Principal.Create("plain-sub", []);

    /// <summary>Passes every gate the fixture sets: the restriction's group, the secret
    /// space's grant, and the eyes-only caveat's nationality.</summary>
    private static Principal Privileged() => Principal.Create(
        "privileged-sub", ["top-secret", "secret-club"],
        [new KeyValuePair<string, IReadOnlyList<string>>("nationality", ["NZ"])]);

    private static AccessRule AccessGrant(Guid spaceId, string expressionJson = """{ "everyone": true }""") => new()
    {
        Kind = AccessRuleKind.AccessGrant,
        SpaceId = spaceId,
        ExpressionJson = expressionJson,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule ViewRestriction(Guid pageId, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = PageAction.View,
        ExpressionJson = expressionJson,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static string Link(Page page) => PageLinkCorpus.Link(page.Id);

    /// <summary>
    /// Two spaces. ENG is open; SEC admits only <c>secret-club</c>. In ENG: <c>a</c> and
    /// <c>b</c> are plainly visible; <c>r</c> carries a view restriction; <c>n</c> carries
    /// an NZ eyes-only caveat; <c>t</c> is in the trash. <c>s</c> lives in SEC. ARCH is an
    /// archived space holding <c>z</c>. Every hidden page links to <c>a</c>, and <c>a</c>
    /// links to every one of them (plus <c>b</c> first and a dangling id last), so each
    /// direction of each kind of hiding is exercised by one fixture.
    /// </summary>
    private sealed class Fixture
    {
        public Space Eng { get; } = TestData.NewSpace("ENG");
        public Space Sec { get; } = TestData.NewSpace("SEC");
        public Space Arch { get; } = TestData.NewSpace("ARCH");
        public Page A { get; }
        public Page B { get; }
        public Page Restricted { get; }
        public Page Caveated { get; }
        public Page Trashed { get; }
        public Page InSecretSpace { get; }
        public Page InArchivedSpace { get; }
        public AccessRule Restriction { get; }

        public Fixture(RocketWikiDbContext context)
        {
            Arch.IsDeleted = true;
            A = TestData.NewPage(Eng, "a");
            B = TestData.NewPage(Eng, "b");
            Restricted = TestData.NewPage(Eng, "r");
            Caveated = TestData.NewPage(Eng, "n");
            Trashed = TestData.NewPage(Eng, "t");
            Trashed.IsDeleted = true;
            InSecretSpace = TestData.NewPage(Sec, "s");
            InArchivedSpace = TestData.NewPage(Arch, "z");

            A.CurrentContent = string.Join(" ",
                Link(B), Link(Restricted), Link(Caveated), Link(Trashed), Link(InSecretSpace), Link(InArchivedSpace),
                PageLinkCorpus.Link(PageLinkCorpus.Dangling));
            B.CurrentContent = Link(A);
            Restricted.CurrentContent = Link(A);
            Caveated.CurrentContent = Link(A);
            Trashed.CurrentContent = Link(A);
            InSecretSpace.CurrentContent = Link(A);
            InArchivedSpace.CurrentContent = Link(A);

            Restriction = ViewRestriction(Restricted.Id, """{ "group": "top-secret" }""");

            context.Spaces.AddRange(Eng, Sec, Arch);
            context.Pages.AddRange(A, B, Restricted, Caveated, Trashed, InSecretSpace, InArchivedSpace);
            context.PageMarkings.Add(TestData.NewMarking(Caveated, ClassificationLevel.Official, "NZ"));
            context.AccessRules.AddRange(
                AccessGrant(Eng.Id), AccessGrant(Sec.Id, """{ "group": "secret-club" }"""), AccessGrant(Arch.Id), Restriction);
            foreach (var page in new[] { A, B, Restricted, Caveated, Trashed, InSecretSpace, InArchivedSpace })
            {
                context.PageLinks.AddRange(PageLink.FromContent(page.Id, page.CurrentContent));
            }

            context.SaveChanges();
        }

        public IEnumerable<Guid> HiddenFromPlain =>
            [Restricted.Id, Caveated.Id, Trashed.Id, InSecretSpace.Id, InArchivedSpace.Id, PageLinkCorpus.Dangling];
    }

    // --- The graph -----------------------------------------------------------------------

    [Fact]
    public async Task GetGraph_OmitsEveryPageTheCallerCannotView_AndEveryEdgeTouchingOne()
    {
        using var context = CreateContext();
        var f = new Fixture(context);

        var graph = await new PageGraphService(context).GetGraphAsync(null, Plain());

        Assert.Equal(new[] { f.A.Id, f.B.Id }.Order(), graph.Nodes.Select(n => n.Id).Order());
        Assert.Equal(
            [new PageGraphEdge(f.A.Id, f.B.Id, 0), new PageGraphEdge(f.B.Id, f.A.Id, 0)],
            graph.Edges.OrderBy(e => e.SourcePageId).ThenBy(e => e.TargetPageId));
        foreach (var hidden in f.HiddenFromPlain)
        {
            Assert.DoesNotContain(graph.Nodes, n => n.Id == hidden);
            Assert.DoesNotContain(graph.Edges, e => e.SourcePageId == hidden || e.TargetPageId == hidden);
        }
    }

    [Fact]
    public async Task GetGraph_ForAPrincipalWhoPassesTheGates_ShowsTheSamePages_SoTheFilterIsThePrincipal()
    {
        // The control for the test above: the fixture's restricted, caveated and secret-space
        // pages ARE in the graph for a principal who passes their gates - proving their
        // absence for the plain principal is the gate's doing, not the fixture's. The trash
        // and the archived space stay absent for everyone.
        using var context = CreateContext();
        var f = new Fixture(context);

        var graph = await new PageGraphService(context).GetGraphAsync(null, Privileged());

        Assert.Equal(
            new[] { f.A.Id, f.B.Id, f.Restricted.Id, f.Caveated.Id, f.InSecretSpace.Id }.Order(),
            graph.Nodes.Select(n => n.Id).Order());
        Assert.Contains(new PageGraphEdge(f.A.Id, f.Restricted.Id, 1), graph.Edges);
        Assert.Contains(new PageGraphEdge(f.Restricted.Id, f.A.Id, 0), graph.Edges);
        Assert.Contains(new PageGraphEdge(f.A.Id, f.Caveated.Id, 2), graph.Edges);
        Assert.Contains(new PageGraphEdge(f.A.Id, f.InSecretSpace.Id, 4), graph.Edges);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == f.Trashed.Id || n.Id == f.InArchivedSpace.Id);
        Assert.DoesNotContain(graph.Edges, e => e.TargetPageId == PageLinkCorpus.Dangling);
    }

    [Fact]
    public async Task GetGraph_EveryEdgeEndpointIsANode()
    {
        using var context = CreateContext();
        _ = new Fixture(context);

        foreach (var principal in new[] { Plain(), Privileged() })
        {
            var graph = await new PageGraphService(context).GetGraphAsync(null, principal);
            var nodeIds = graph.Nodes.Select(n => n.Id).ToHashSet();
            Assert.All(graph.Edges, e =>
            {
                Assert.Contains(e.SourcePageId, nodeIds);
                Assert.Contains(e.TargetPageId, nodeIds);
            });
        }
    }

    [Fact]
    public async Task GetGraph_ANodeCarriesTheSpaceKeyAndTheMarkingTheGatePassedOn()
    {
        using var context = CreateContext();
        var f = new Fixture(context);

        var graph = await new PageGraphService(context).GetGraphAsync(null, Privileged());

        var caveated = Assert.Single(graph.Nodes, n => n.Id == f.Caveated.Id);
        Assert.Equal("ENG", caveated.SpaceKey);
        Assert.Equal("n", caveated.Slug);
        Assert.Equal(["NZ"], caveated.Marking.EyesOnly);
        Assert.Equal(ClassificationLevel.Official, caveated.Marking.Level);
    }

    [Fact]
    public async Task GetGraph_FilteredToASpace_IsTheSubgraphInducedOnThatSpace()
    {
        using var context = CreateContext();
        var f = new Fixture(context);
        var service = new PageGraphService(context);

        // SEC for a privileged caller: s alone, and its link out to ENG/a has no node to end
        // on inside the filtered view, so there is no edge.
        var sec = await service.GetGraphAsync(f.Sec.Id, Privileged());
        Assert.Equal([f.InSecretSpace.Id], sec.Nodes.Select(n => n.Id));
        Assert.Empty(sec.Edges);

        // ENG for the same caller: a, b, r, n - and the edge a -> s is gone with s.
        var eng = await service.GetGraphAsync(f.Eng.Id, Privileged());
        Assert.Equal(
            new[] { f.A.Id, f.B.Id, f.Restricted.Id, f.Caveated.Id }.Order(),
            eng.Nodes.Select(n => n.Id).Order());
        Assert.DoesNotContain(eng.Edges, e => e.TargetPageId == f.InSecretSpace.Id);
        Assert.Contains(new PageGraphEdge(f.A.Id, f.Restricted.Id, 1), eng.Edges);
    }

    [Fact]
    public async Task GetGraph_ASpaceTheCallerCannotEnter_AnArchivedSpace_AndANonexistentSpace_AreTheSameEmptyGraph()
    {
        using var context = CreateContext();
        var f = new Fixture(context);
        var service = new PageGraphService(context);

        var noAccess = await service.GetGraphAsync(f.Sec.Id, Plain());
        var archived = await service.GetGraphAsync(f.Arch.Id, Privileged());
        var missing = await service.GetGraphAsync(Guid.NewGuid(), Privileged());

        Assert.Equal(PageGraph.Empty, noAccess);
        Assert.Equal(PageGraph.Empty, archived);
        Assert.Equal(PageGraph.Empty, missing);
    }

    [Fact]
    public async Task GetGraph_ATrashedPage_ReturnsToTheGraphWhenRestored_ItsIndexRowsWereKept()
    {
        // The index keeps a trashed page's rows (no query filter); the read path hides them.
        // Restoring flips IsDeleted and touches no content, so the links must come back
        // without a re-index.
        using var context = CreateContext();
        var f = new Fixture(context);
        var service = new PageGraphService(context);

        Assert.DoesNotContain((await service.GetGraphAsync(null, Plain())).Nodes, n => n.Id == f.Trashed.Id);

        f.Trashed.IsDeleted = false;
        context.SaveChanges();

        var graph = await service.GetGraphAsync(null, Plain());
        Assert.Contains(graph.Nodes, n => n.Id == f.Trashed.Id);
        Assert.Contains(new PageGraphEdge(f.Trashed.Id, f.A.Id, 0), graph.Edges);
        Assert.Contains(new PageGraphEdge(f.A.Id, f.Trashed.Id, 3), graph.Edges);
    }

    // --- Per page ------------------------------------------------------------------------

    [Fact]
    public async Task GetPageLinks_AHiddenBacklinkIsNeitherListedNorCounted()
    {
        // Five pages link to a; the plain caller may view one of them. The count is the
        // list's length, so it is 1 - not "1 shown of 5", which would tell the caller four
        // pages they cannot see exist.
        using var context = CreateContext();
        var f = new Fixture(context);

        var result = await new PageGraphService(context).GetPageLinksAsync(f.A.Id, Plain());

        var found = Assert.IsType<ReadResult<PageLinkNeighbours>.Found>(result);
        Assert.Equal([f.B.Id], found.Value.Inbound.Select(n => n.Id));
        Assert.Equal(1, found.Value.InboundCount);
        foreach (var hidden in f.HiddenFromPlain)
        {
            Assert.DoesNotContain(found.Value.Inbound, n => n.Id == hidden);
            Assert.DoesNotContain(found.Value.Outbound, n => n.Id == hidden);
        }
    }

    [Fact]
    public async Task GetPageLinks_AHiddenTargetIsNeitherListedNorCounted_AndOutboundKeepsFirstOccurrenceOrder()
    {
        using var context = CreateContext();
        var f = new Fixture(context);
        var service = new PageGraphService(context);

        var plain = Assert.IsType<ReadResult<PageLinkNeighbours>.Found>(await service.GetPageLinksAsync(f.A.Id, Plain()));
        Assert.Equal([f.B.Id], plain.Value.Outbound.Select(n => n.Id));
        Assert.Equal(1, plain.Value.OutboundCount);

        // The privileged caller sees the targets they pass the gates for, in the order a's
        // content names them; the trash, the archived space and the dangling id are still
        // not pages.
        var privileged = Assert.IsType<ReadResult<PageLinkNeighbours>.Found>(await service.GetPageLinksAsync(f.A.Id, Privileged()));
        Assert.Equal(
            [f.B.Id, f.Restricted.Id, f.Caveated.Id, f.InSecretSpace.Id],
            privileged.Value.Outbound.Select(n => n.Id));
        Assert.Equal(4, privileged.Value.OutboundCount);
        Assert.Equal(
            new[] { f.B.Id, f.Restricted.Id, f.Caveated.Id, f.InSecretSpace.Id }.Order(),
            privileged.Value.Inbound.Select(n => n.Id).Order());
    }

    [Fact]
    public async Task GetPageLinks_ForAPageTheCallerCannotView_IsDeniedWithTheGateReason_NothingAboutItsLinksTravels()
    {
        using var context = CreateContext();
        var f = new Fixture(context);
        var service = new PageGraphService(context);

        var restricted = await service.GetPageLinksAsync(f.Restricted.Id, Plain());
        var noSpaceAccess = await service.GetPageLinksAsync(f.InSecretSpace.Id, Plain());

        Assert.Equal($"restriction:{f.Restricted.Id}:{f.Restriction.Id}", Assert.IsType<ReadResult<PageLinkNeighbours>.Denied>(restricted).Reason);
        Assert.Equal(EffectivePermissionCalculator.NoSpaceAccessReason, Assert.IsType<ReadResult<PageLinkNeighbours>.Denied>(noSpaceAccess).Reason);
    }

    [Fact]
    public async Task GetPageLinks_MissingTrashedAndArchived_AreNotFound()
    {
        using var context = CreateContext();
        var f = new Fixture(context);
        var service = new PageGraphService(context);

        Assert.IsType<ReadResult<PageLinkNeighbours>.NotFound>(await service.GetPageLinksAsync(Guid.NewGuid(), Privileged()));
        Assert.IsType<ReadResult<PageLinkNeighbours>.NotFound>(await service.GetPageLinksAsync(f.Trashed.Id, Privileged()));
        Assert.IsType<ReadResult<PageLinkNeighbours>.NotFound>(await service.GetPageLinksAsync(f.InArchivedSpace.Id, Privileged()));
    }

    [Fact]
    public async Task GetPageLinks_APageWithNoLinksEitherWay_IsFoundAndEmpty()
    {
        using var context = CreateContext();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "lonely");
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(AccessGrant(space.Id));
        context.SaveChanges();

        var result = await new PageGraphService(context).GetPageLinksAsync(page.Id, Plain());

        var found = Assert.IsType<ReadResult<PageLinkNeighbours>.Found>(result);
        Assert.Empty(found.Value.Outbound);
        Assert.Empty(found.Value.Inbound);
        Assert.Equal(0, found.Value.InboundCount);
    }
}
