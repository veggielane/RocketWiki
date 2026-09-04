using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using RocketWiki.Core.Tests.Access;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §21: protective markings as they behave against a real database — the
/// every-page-is-marked invariant, inheritance at creation, the write-side rules on
/// re-marking, and the read paths that must exclude a page the caller is not granted
/// ENTIRELY rather than merely rank it lower or count it.
///
/// <para>The level used to be the denying fact in most of these fixtures — a SECRET page,
/// a caller with no clearance. This deployment carries no clearance attribute, so the
/// level gates nothing and those fixtures now deny by a selector the caller is not
/// granted or a caveat they are not in; the cases that proved the level denied are
/// replaced by ones that prove it does not.</para>
/// </summary>
public class PageMarkingTests : SqliteTestBase
{
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal PrincipalWith(string[]? nationality = null, string[]? groups = null)
    {
        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        if (nationality is not null)
        {
            attributes.Add(new("nationality", nationality));
        }

        return Principal.Create("user-sub", groups ?? [], attributes);
    }

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

    /// <summary>An access grant for everyone that confers the given selector values
    /// (design.md §21.15) - the whole of what a reader of a selector-bearing page needs.</summary>
    private static AccessRule AccessGrantWith(Guid spaceId, params SelectorValue[] selectors)
    {
        var grant = Grant(spaceId, null);
        foreach (var selector in selectors)
        {
            grant.Selectors.Add(new AccessRuleSelector { AccessRuleId = grant.Id, Category = selector.Category, Value = selector.Value });
        }

        return grant;
    }

    // --- The every-page-is-marked invariant ---------------------------------------------

    [Fact]
    public void AnyPageInsertedWithoutAMarking_GetsOne_AtOfficial()
    {
        // The invariant is enforced at the persistence seam (RocketWikiDbContext), not by
        // each write path remembering - so a code path that never heard of markings still
        // cannot commit an unmarked page.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        var marking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == page.Id);
        Assert.Equal(ClassificationLevel.Official, marking.Level);
        Assert.Empty(marking.Countries);
        // Nobody chose this marking, the invariant did - so there is no actor to name.
        Assert.Null(marking.SetByUserId);
    }

    [Fact]
    public void APageInsertedAlongsideItsParentInOneUnitOfWork_InheritsTheParentsMarking()
    {
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");
        var child = TestData.NewPage(space, "child", parent);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.Secret, "UK"));
        context.Pages.Add(child);
        context.SaveChanges();

        var childMarking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == child.Id);
        Assert.Equal(ClassificationLevel.Secret, childMarking.Level);
        Assert.Equal(["UK"], childMarking.Countries.Select(c => c.CountryValue));
    }

    [Fact]
    public async Task CreatePage_UnderAMarkedParent_InheritsThatMarking()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.Secret, "UK", "US"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith(["UK"]), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        var marking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == result.Value.Id);
        Assert.Equal(ClassificationLevel.Secret, marking.Level);
        Assert.Equal(["UK", "US"], marking.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreatePage_UnderAParentWithSelectors_InheritsThem()
    {
        // design.md §21.15: selectors inherit exactly like the level and the caveat. The
        // creator must pass the parent's full marking - granted APPLE and NORTH in this
        // space - to create beneath it at all (§6.4: canEdit falls through canView); what
        // this pins is that the child's ROWS then carry the parent's selectors, so nothing
        // widens at creation.
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(
            TestData.NewMarking(parent, ClassificationLevel.Secret, "UK").WithSelectors(TestCatalogs.Apple, TestCatalogs.North));
        context.AccessRules.AddRange(AccessGrantWith(space.Id, TestCatalogs.Apple, TestCatalogs.North), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith(["UK"]), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        using var readContext = CreateContext();
        var marking = readContext.PageMarkings.Include(m => m.Countries).Include(m => m.Selectors)
            .Single(m => m.PageId == result.Value.Id);
        Assert.Equal([TestCatalogs.Apple, TestCatalogs.North], marking.ToMarking().Selectors);
        Assert.Equal("UK SECRET APPLE NORTH UK EYES ONLY", marking.ToMarking().Format(TestCatalogs.Fruit));
    }

    [Fact]
    public void AnyPageInsertedAlongsideItsParent_InheritsTheParentsSelectors()
    {
        // The persistence-seam backstop (RocketWikiDbContext.EnsurePageMarkings) copies
        // selectors as well as countries: a code path that never heard of selectors still
        // cannot commit a child that dropped its parent's.
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");
        var child = TestData.NewPage(space, "child", parent);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.Official).WithSelectors(TestCatalogs.Banana));
        context.Pages.Add(child);
        context.SaveChanges();

        using var readContext = CreateContext();
        var childMarking = readContext.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == child.Id);
        Assert.Equal([TestCatalogs.Banana], childMarking.ToMarking().Selectors);
    }

    /// <summary>
    /// design.md §21.11 nominates <c>SetByUserId IS NULL</c> as "the query that finds
    /// every page nobody has yet looked at", so a marking nobody chose must not name an
    /// actor. Creation inherits — the author picked a parent, not a classification, which
    /// is also why §21.7 raises no marking event for a page creation.
    ///
    /// <para>Naming the creator made every page ever created look reviewed. It matters
    /// most where unreviewed content arrives in bulk: a Confluence import creates every
    /// page through this method with a real acting user, so an entire migrated estate was
    /// stamped non-null and invisible to that query — strictly worse than the
    /// AddPageMarkings backfill it parallels, which leaves NULL. Both other writers of
    /// this column (the persistence-seam backstop and the sync importer) already wrote
    /// null; this one disagreed.</para>
    /// </summary>
    [Fact]
    public async Task CreatePage_LeavesTheMarkingUnattributed_SoUnreviewedPagesStayFindable()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var created = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "fresh", "Fresh", "# Fresh"),
            PrincipalWith(["UK"]), author.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var inheritedMarking = context.PageMarkings.Single(m => m.PageId == created.Value.Id);
        Assert.Null(inheritedMarking.SetByUserId);

        // Non-vacuous, and the whole point of the distinction: an EXPLICIT re-mark is a
        // judgement, so it does name its actor and drops out of the unreviewed query.
        var marked = await new PageMarkingService(context, "local-instance").SetAsync(
            new SetPageMarkingRequest(created.Value.Id, ClassificationLevel.Secret, [], [], UkPrefix: true),
            PrincipalWith(["UK"]), author.Id, AuditCtx);
        Assert.True(marked.IsSuccess, $"re-mark failed: {marked.Error}");

        Assert.Equal(author.Id, context.PageMarkings.Single(m => m.PageId == created.Value.Id).SetByUserId);
    }

    [Fact]
    public async Task CreatePage_AtTheRoot_IsOfficial()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "root", "Root", "# Root"),
            PrincipalWith(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(ClassificationLevel.Official, context.PageMarkings.Single(m => m.PageId == result.Value.Id).Level);
    }

    [Fact]
    public async Task CreatePage_UnderAParentTheCallerIsNotGranted_IsRefused()
    {
        // Creating a page you could not then read is impossible by construction: the
        // create's canEdit check runs against the marking the new page will inherit. The
        // parent carries APPLE and the caller's access grant confers nothing.
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.TopSecret).WithSelectors(TestCatalogs.Apple));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith(), Guid.NewGuid(), AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("selector:not_granted:FRUIT", Assert.IsType<ForbiddenError>(result.Error).Reason);
    }

    // --- The national prefix (design.md §21.12) ------------------------------------------

    [Fact]
    public void AMarkingMaterializedByTheBackstop_CarriesTheUkDefault()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.SaveChanges();

        Assert.Equal("UK", context.PageMarkings.Single(m => m.PageId == page.Id).Prefix);
    }

    [Fact]
    public async Task CreatePage_InheritsTheParentsPrefix_NotJustTheInstanceDefault()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(TestData.NewMarkingWithPrefix(parent, ClassificationLevel.Official, "NATO"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("NATO", context.PageMarkings.Single(m => m.PageId == result.Value.Id).Prefix);
    }

    [Fact]
    public async Task SetMarking_PrefixIsAToggle_UkOrNone()
    {
        // design.md §21.12: the prefix is UK or nothing, a toggle rather than free text.
        // On: the label leads with UK and the audit row records it. Off: the bare level -
        // a legal marking, and clearing it changes nobody's access, so it stays an
        // ordinary page.marking.set rather than diluting the downgrade query that exists
        // to find real widenings.
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var on = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Secret, ["UK"], [], UkPrefix: true),
            PrincipalWith(["UK"]), f.ActingUserId, AuditCtx);

        Assert.True(on.IsSuccess);
        Assert.True(on.Value.UkPrefix);
        Assert.Equal("UK SECRET UK EYES ONLY", on.Value.Label);
        Assert.Equal("UK", context.PageMarkings.Single(m => m.PageId == f.Page.Id).Prefix);

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        Assert.Contains("\"prefix\":\"UK\"", audit.DetailsJson);
        Assert.Contains("\"previousPrefix\":\"UK\"", audit.DetailsJson);

        var off = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Secret, ["UK"], [], UkPrefix: false),
            PrincipalWith(["UK"]), f.ActingUserId, AuditCtx);

        Assert.True(off.IsSuccess);
        Assert.False(off.Value.UkPrefix);
        Assert.Equal("SECRET UK EYES ONLY", off.Value.Label);
        Assert.Null(context.PageMarkings.Single(m => m.PageId == f.Page.Id).Prefix);
        Assert.Equal(2, context.AuditEvents.Count(e => e.Action == "page.marking.set"));
        Assert.Empty(context.AuditEvents.Where(e => e.Action == "page.marking.downgrade"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UK")]
    [InlineData("ZZNONSENSEZZ")]
    public async Task ThePrefix_ChangesNoAccessDecision(string? prefix)
    {
        // The end-to-end half of CaveatGateTests' invariance proof: the SAME page, read
        // through the real permission loader, gives identical answers to an admitted and
        // an excluded principal whatever prefix it carries.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarkingWithPrefix(page, ClassificationLevel.Secret, prefix, "UK"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageReadService(context);

        Assert.IsType<ReadResult<Page>.Found>(await service.GetPageAsync(page.Id, PrincipalWith(["UK"])));

        var deniedByCaveat = Assert.IsType<ReadResult<Page>.Denied>(await service.GetPageAsync(page.Id, PrincipalWith(["NZ"])));
        Assert.Equal("caveat:eyes_only", deniedByCaveat.Reason);
    }

    // --- Read paths ----------------------------------------------------------------------

    [Fact]
    public async Task GetPage_TheLevelGatesNothing_EveryLevelIsReadableWithAccess()
    {
        // The retired invariant, inverted and pinned: a caller with nothing but an access
        // grant reads a page at every level, TOP SECRET included, because this deployment
        // carries no clearance to compare against and the level is presentational
        // (§21.12). If the level ever gates again, this goes red.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.TopSecret));
        context.AccessRules.Add(Grant(space.Id, null));
        context.SaveChanges();

        var service = new PageReadService(context);
        foreach (var level in Enum.GetValues<ClassificationLevel>())
        {
            context.PageMarkings.Single(m => m.PageId == page.Id).Level = level;
            context.SaveChanges();

            Assert.IsType<ReadResult<Page>.Found>(await service.GetPageAsync(page.Id, PrincipalWith()));
        }
    }

    [Fact]
    public async Task GetPage_MissingMarkingRow_IsUnavailable_AndDeniedToEveryone()
    {
        // Belt and braces against a future code path that forgets. The row is removed
        // AFTER the save that materialized it, which is the only way to reach this state.
        // Nobody reads the page - not a Space-admin, not a caller granted every selector,
        // not a five-eyes national - because the sentinel denies by its own gate, not by
        // a level that no longer gates.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.AddRange(
            AccessGrantWith(space.Id, TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South),
            Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        context.PageMarkings.Remove(context.PageMarkings.Single(m => m.PageId == page.Id));
        context.SaveChanges();

        var service = new PageReadService(context);

        foreach (var caller in new[] { PrincipalWith(), PrincipalWith(["UK"]), PrincipalWith(["AUS", "CAN", "NZ", "UK", "US"], ["engineering"]) })
        {
            var denied = Assert.IsType<ReadResult<Page>.Denied>(await service.GetPageAsync(page.Id, caller));
            Assert.Equal("marking:unavailable", denied.Reason);
        }
    }

    [Fact]
    public async Task GetPage_EyesOnly_RequiresAMatchingNationality()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "UK", "US"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageReadService(context);

        Assert.IsType<ReadResult<Page>.Found>(await service.GetPageAsync(page.Id, PrincipalWith(["US"])));

        var denied = Assert.IsType<ReadResult<Page>.Denied>(await service.GetPageAsync(page.Id, PrincipalWith(["NZ"])));
        Assert.Equal("caveat:eyes_only", denied.Reason);

        // No nationality at all: denied, fail closed like any attr condition.
        Assert.IsType<ReadResult<Page>.Denied>(await service.GetPageAsync(page.Id, PrincipalWith()));
    }

    [Fact]
    public async Task PageTree_ExcludesAnUngrantedPage_AndItsSubtree_Entirely()
    {
        var space = TestData.NewSpace();
        var open = TestData.NewPage(space, "open");
        var compartment = TestData.NewPage(space, "compartment");
        var childOfCompartment = TestData.NewPage(space, "child", compartment);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(open, compartment, childOfCompartment);
        context.PageMarkings.Add(TestData.NewMarking(compartment, ClassificationLevel.TopSecret).WithSelectors(TestCatalogs.Apple));
        // The child is deliberately plain: it is pruned because its parent is, not
        // because of its own marking - a tree cannot render a node whose parent is absent.
        context.PageMarkings.Add(TestData.NewMarking(childOfCompartment, ClassificationLevel.Official));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageReadService(context);
        var found = Assert.IsType<ReadResult<IReadOnlyList<PageTreeEntry>>.Found>(
            await service.GetPageTreeAsync(space.Id, PrincipalWith()));

        var node = Assert.Single(found.Value.OfType<PageTreeNode>());
        Assert.Equal(open.Id, node.Id);
        Assert.Empty(node.Children);

        // The compartment root is a protected leaf at its position (design.md §6.7/§21.8):
        // its denial names the selector, and nothing beneath it is walked - the plain
        // child is neither a node nor a placeholder, because a tree cannot render a
        // child of a page the caller cannot see.
        var placeholder = Assert.Single(found.Value.OfType<ProtectedTreeNode>());
        Assert.Equal("selector:not_granted:FRUIT", placeholder.Denial.Reason);
        Assert.False(placeholder.Denial.NoSpaceAccess);
        Assert.Equal(ClassificationLevel.TopSecret, placeholder.Denial.Marking!.Level);
    }

    [Fact]
    public async Task PageTree_CarriesEachVisibleNodesMarking_TheSameValueThePruningUsed()
    {
        // The tree is a listing surface, and every node in it already passed the marking
        // gate for the marking reported here - so this exposes what the walk computed
        // rather than loading it a second time (design.md §21.9). A node showing a
        // different marking from the one it was gated on would be the wrong kind of wrong.
        var space = TestData.NewSpace();
        var root = TestData.NewPage(space, "root");
        var child = TestData.NewPage(space, "child", root);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(root, child);
        context.PageMarkings.Add(TestData.NewMarkingWithPrefix(root, ClassificationLevel.Official, "UK"));
        context.PageMarkings.Add(TestData.NewMarkingWithPrefix(child, ClassificationLevel.Secret, "NATO", "UK", "US"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageReadService(context);
        var found = Assert.IsType<ReadResult<IReadOnlyList<PageTreeEntry>>.Found>(
            await service.GetPageTreeAsync(space.Id, PrincipalWith(["UK"])));

        var rootNode = Assert.IsType<PageTreeNode>(Assert.Single(found.Value));
        Assert.Equal("UK OFFICIAL", rootNode.Marking.Label);
        Assert.Equal(ClassificationLevel.Official, rootNode.Marking.Level);

        var childNode = Assert.IsType<PageTreeNode>(Assert.Single(rootNode.Children));
        Assert.Equal("NATO SECRET UK/US EYES ONLY", childNode.Marking.Label);
        Assert.Equal(["UK", "US"], childNode.Marking.EyesOnly);
    }

    [Fact]
    public async Task PageTree_APageWhoseMarkingRowIsMissing_IsPrunedForEveryone_AndNeverReportsABlankMarking()
    {
        // The fail-closed guard reaching the listing surface: a page with no marking row
        // is unavailable, so it is a placeholder for every caller - and the placeholder
        // reports the sentinel's bare TOP SECRET rather than an empty badge that would
        // misrepresent the enforcement they are subject to.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "orphaned");

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.AddRange(
            AccessGrantWith(space.Id, TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South),
            Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        context.PageMarkings.Remove(context.PageMarkings.Single(m => m.PageId == page.Id));
        context.SaveChanges();

        var service = new PageReadService(context);

        foreach (var caller in new[] { PrincipalWith(), PrincipalWith(["AUS", "CAN", "NZ", "UK", "US"], ["engineering"]) })
        {
            var pruned = Assert.IsType<ReadResult<IReadOnlyList<PageTreeEntry>>.Found>(
                await service.GetPageTreeAsync(space.Id, caller));
            Assert.Empty(pruned.Value.OfType<PageTreeNode>());
            var placeholder = Assert.IsType<ProtectedTreeNode>(Assert.Single(pruned.Value));
            Assert.Equal("marking:unavailable", placeholder.Denial.Reason);
            Assert.Equal("TOP SECRET", placeholder.Denial.Marking!.Format(TestCatalogs.Fruit));
        }
    }

    [Fact]
    public async Task Search_ExcludesAnUngrantedPage_TitleAndSnippetNeverBuilt()
    {
        var space = TestData.NewSpace();
        var open = TestData.NewPage(space, "open");
        open.Title = "Nozzle geometry";
        open.CurrentContent = "# Nozzle geometry\n\nExpansion ratio notes.";
        var compartment = TestData.NewPage(space, "compartment");
        compartment.Title = "Nozzle geometry ZZSECRETTITLEZZ";
        compartment.CurrentContent = "# Nozzle geometry\n\nZZSECRETBODYZZ expansion ratio.";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(open, compartment);
        context.PageMarkings.Add(TestData.NewMarking(compartment, ClassificationLevel.Secret).WithSelectors(TestCatalogs.Apple));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new SearchService(context, NullLogger<SearchService>.Instance);
        var hits = await service.SearchAsync(new SearchRequest("expansion", null, null), PrincipalWith(), maxResults: 10);

        // Absent entirely, not merely ranked lower and not implied by a count - and the
        // snippet builder never ran over its content, which is where a leak would show.
        Assert.Equal(open.Id, Assert.Single(hits).PageId);
        var serialized = string.Join("|", hits.Select(h => $"{h.Title}|{h.Snippet}"));
        Assert.DoesNotContain("ZZSECRETTITLEZZ", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZSECRETBODYZZ", serialized, StringComparison.Ordinal);
    }

    // --- Setting a marking ---------------------------------------------------------------

    private sealed record Fixture(Space Space, Page Page, Guid ActingUserId);

    private Fixture Seed(RocketWikiDbContext context, ClassificationLevel level = ClassificationLevel.Official)
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, level));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        return new Fixture(space, page, user.Id);
    }

    [Fact]
    public async Task SetMarking_RaisingTheLevel_AuditsAsSet_AndPersists()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Secret, ["uk", "US"], [], UkPrefix: true),
            PrincipalWith(["UK"]), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("UK SECRET UK/US EYES ONLY", result.Value.Label);

        var marking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == f.Page.Id);
        Assert.Equal(ClassificationLevel.Secret, marking.Level);
        Assert.Equal(["UK", "US"], marking.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(f.ActingUserId, marking.SetByUserId);

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        Assert.Equal("page.marking.set", audit.Action);
        Assert.Equal(AuditSubjectType.Page, audit.SubjectType);
        Assert.Equal(f.Page.Id, audit.SubjectId);
        Assert.Contains("\"level\":\"SECRET\"", audit.DetailsJson);
        Assert.Contains("\"previousLevel\":\"OFFICIAL\"", audit.DetailsJson);
    }

    [Fact]
    public async Task SetMarking_LoweringTheLevel_AuditsAsDowngrade_WithTheFromToInDetails()
    {
        using var context = CreateContext();
        var f = Seed(context, ClassificationLevel.Secret);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        // Its own action name, so a reviewer can find every declassification with one
        // query - the level gates nothing here, but lowering what content is declared to
        // be is still the judgement the downgrade query exists to surface.
        Assert.Equal("page.marking.downgrade", audit.Action);
        Assert.Contains("\"previousLevel\":\"SECRET\"", audit.DetailsJson);
        Assert.Contains("\"level\":\"OFFICIAL\"", audit.DetailsJson);
    }

    [Fact]
    public async Task SetMarking_ClearingTheEyesOnlyCaveat_IsADowngrade()
    {
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "UK"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, [], [], UkPrefix: true),
            PrincipalWith(["UK"]), user.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(context.PageMarkingCountries.Where(c => c.PageId == page.Id));
        Assert.Equal("page.marking.downgrade", context.AuditEvents.Single(e => e.Action.StartsWith("page.marking")).Action);
    }

    [Fact]
    public async Task SetMarking_ToAnyLevel_IsAllowed_TheLevelIsUnconstrained()
    {
        // The retired self-lockout case, inverted: "above your own clearance" used to be
        // Forbidden, and now there is no clearance - the level is presentational (§21.12),
        // so an editor with no attributes at all may set TOP SECRET and can still read
        // the result. The self-lockout rule is about selectors and the caveat only.
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.TopSecret, [], [], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess, $"{result.Error}");
        Assert.Equal("UK TOP SECRET", result.Value.Label);
        Assert.Equal(ClassificationLevel.TopSecret, context.PageMarkings.Single(m => m.PageId == f.Page.Id).Level);
        Assert.IsType<ReadResult<Page>.Found>(await new PageReadService(context).GetPageAsync(f.Page.Id, PrincipalWith()));
    }

    [Fact]
    public async Task SetMarking_ToAnEyesOnlySetYouAreNotIn_IsForbidden()
    {
        // The self-lockout rule (§21.6): you may not mark a page out of your own reach. A
        // UK editor marking a page US EYES ONLY loses it completely.
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, ["US"], [], UkPrefix: true),
            PrincipalWith(["UK"]), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("caveat:eyes_only", Assert.IsType<ForbiddenError>(result.Error).Reason);
    }

    [Fact]
    public async Task SetMarking_ACountryOutsideTheFixedSet_IsRefused()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, ["FR"], [], UkPrefix: true),
            PrincipalWith(["FR"]), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains("FR", error.Message, StringComparison.Ordinal);
        // The fixed set is named so the message is actionable (design.md §21.4).
        Assert.Contains("AUS, CAN, NZ, UK, US", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetMarking_WithoutCanEdit_IsForbidden()
    {
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(Grant(space.Id, null));
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, [], [], UkPrefix: true),
            PrincipalWith(), user.Id, AuditCtx);

        Assert.Equal("canEdit required", Assert.IsType<ForbiddenError>(result.Error).Reason);
    }

    [Fact]
    public async Task SetMarking_OnAReplicaSpace_IsRefusedBeneathEveryGrant()
    {
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        space.OriginInstanceId = "some-other-instance";
        var page = TestData.NewPage(space);
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, [], [], UkPrefix: true),
            PrincipalWith(), user.Id, AuditCtx);

        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    [Fact]
    public async Task SetMarking_OnAPageYouCannotSee_IsRefusedWithoutRevealingItExists()
    {
        // canEdit already includes the marking gate against the page's CURRENT marking,
        // so a page you cannot read is a page you cannot re-mark - including re-marking it
        // to make it readable. The page carries APPLE; the caller's grant confers nothing.
        using var context = CreateContext();
        var f = Seed(context, ClassificationLevel.TopSecret);
        context.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == f.Page.Id).WithSelectors(TestCatalogs.Apple);
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.Equal("canEdit required", Assert.IsType<ForbiddenError>(result.Error).Reason);
        Assert.Equal(ClassificationLevel.TopSecret, context.PageMarkings.Single(m => m.PageId == f.Page.Id).Level);
        Assert.Single(context.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id));
    }

    [Fact]
    public async Task SetMarking_ReplacingTheCountrySet_RemovesWhatIsNoLongerWanted()
    {
        using var context = CreateContext();
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "UK", "US"));
        context.AccessRules.AddRange(Grant(space.Id, null), Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, ["NZ", "UK"], [], UkPrefix: true),
            PrincipalWith(["UK"]), user.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["NZ", "UK"],
            context.PageMarkingCountries.Where(c => c.PageId == page.Id)
                .Select(c => c.CountryValue).ToList().OrderBy(c => c, StringComparer.Ordinal));
    }

    // --- Selector gates on the read and write paths (design.md §21.15, Phase E) ---------

    [Fact]
    public async Task GetPage_SelectorNotGranted_IsDeniedWithTheSelectorReason()
    {
        using var context = CreateContext();
        var f = Seed(context);
        context.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == f.Page.Id).WithSelectors(TestCatalogs.Apple);
        context.SaveChanges();

        var readService = new PageReadService(context);

        // No access grant in this space carries APPLE - and nothing about the principal
        // could make up for that: there is no claim a selector reads.
        var denied = Assert.IsType<ReadResult<Page>.Denied>(await readService.GetPageAsync(f.Page.Id, PrincipalWith(["UK", "US"], ["engineering"])));
        Assert.Equal("selector:not_granted:FRUIT", denied.Reason);
    }

    [Fact]
    public async Task GetPage_RoleGrantWithoutAccess_IsDenied_RolesNeverSupersedeAccess()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin)); // the highest role, and no access grant
        context.SaveChanges();

        var denied = Assert.IsType<ReadResult<Page>.Denied>(
            await new PageReadService(context).GetPageAsync(page.Id, PrincipalWith()));
        Assert.Equal("no-space-access", denied.Reason);
    }

    [Fact]
    public async Task GetPage_AccessGrantWithoutRole_CanViewNotEdit()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(Grant(space.Id, null));
        context.SaveChanges();

        Assert.IsType<ReadResult<Page>.Found>(await new PageReadService(context).GetPageAsync(page.Id, PrincipalWith()));
        var facts = await new PagePermissionReadService(context, "local-instance").GetPermissionFactsAsync([page.Id], PrincipalWith());
        Assert.True(facts[page.Id].Permission.CanView);
        Assert.False(facts[page.Id].Permission.CanEdit);
        Assert.Equal("insufficient-space-role", facts[page.Id].Permission.EditDenialReason);
        Assert.True(facts[page.Id].HasSpaceAccess);
        Assert.Null(facts[page.Id].SpaceRole);
    }

    // --- The write side of selectors (design.md §21.15, Phase G) ---------------------------

    private static PageMarkingService MarkingService(RocketWikiDbContext context) => new(context, "local-instance");

    [Fact]
    public async Task SetMarking_ASelectorFromAnUnknownCategory_IsRefused()
    {
        using var context = CreateContext();
        var f = Seed(context);
        context.AccessRules.Add(AccessGrantWith(f.Space.Id, TestCatalogs.Apple));
        context.SaveChanges();

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [new SelectorValue("CODEWORD", "ZEBRA")], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains("CODEWORD", error.Message, StringComparison.Ordinal);
        Assert.Contains("FRUIT [APPLE|BANANA]", error.Message, StringComparison.Ordinal); // the configured vocabulary, so the message is actionable
        Assert.Empty(context.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id));
    }

    [Fact]
    public async Task SetMarking_AnUnknownValueInAKnownCategory_IsRefused()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [new SelectorValue("FRUIT", "CHERRY")], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Contains("CHERRY", Assert.IsType<ValidationError>(result.Error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetMarking_TwoValuesInOneCategory_IsRefused()
    {
        // A grant may confer APPLE and BANANA; a page is one or the other (the PK says so).
        // Refused as a validation error naming the category, never a DbUpdateException.
        using var context = CreateContext();
        var f = Seed(context);
        context.AccessRules.Add(AccessGrantWith(f.Space.Id, TestCatalogs.Apple, TestCatalogs.Banana));
        context.SaveChanges();

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [TestCatalogs.Apple, TestCatalogs.Banana], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains("FRUIT", error.Message, StringComparison.Ordinal);
        Assert.Contains("at most one value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetMarking_ToASelectorYouAreNotGrantedInThisSpace_IsForbidden()
    {
        // §21.6's self-lockout through the full MarkingGate: no access grant in this space
        // carries APPLE, so the granted union is empty and the gate refuses with the grant
        // token. Forbidden, not validation - the input is well-formed.
        using var context = CreateContext();
        var f = Seed(context);

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [TestCatalogs.Apple], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("selector:not_granted:FRUIT", Assert.IsType<ForbiddenError>(result.Error).Reason);
        Assert.Empty(context.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id));
    }

    [Fact]
    public async Task SetMarking_AddingASelector_PersistsTheRow_AndIsNotADowngrade()
    {
        using var context = CreateContext();
        var f = Seed(context);
        context.AccessRules.Add(AccessGrantWith(f.Space.Id, TestCatalogs.Apple, TestCatalogs.North));
        context.SaveChanges();

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [TestCatalogs.North, TestCatalogs.Apple], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal([TestCatalogs.Apple, TestCatalogs.North], result.Value.Selectors);
        Assert.Equal("UK OFFICIAL APPLE NORTH", result.Value.Label);

        using var readContext = CreateContext();
        var rows = readContext.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id).OrderBy(x => x.Category).ToList();
        Assert.Equal(["FRUIT", "REGION"], rows.Select(r => r.Category));
        Assert.Equal(["APPLE", "NORTH"], rows.Select(r => r.Value));

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        Assert.Equal("page.marking.set", audit.Action); // narrowing the audience is never a downgrade
        Assert.Contains("\"selectors\":{\"FRUIT\":\"APPLE\",\"REGION\":\"NORTH\"}", audit.DetailsJson);
        Assert.Contains("\"previousSelectors\":{}", audit.DetailsJson);
    }

    [Fact]
    public async Task SetMarking_RemovingASelector_AuditsAsDowngrade_WithSelectorsInDetails()
    {
        // Removing a compartment lets somebody read the page who could not before -
        // exactly what page.marking.downgrade exists to find - and the audit row carries
        // both selector sets in full (§21.7).
        using var context = CreateContext();
        var f = Seed(context);
        context.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == f.Page.Id).WithSelectors(TestCatalogs.Apple, TestCatalogs.North);
        context.AccessRules.Add(AccessGrantWith(f.Space.Id, TestCatalogs.Apple, TestCatalogs.North));
        context.SaveChanges();

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [TestCatalogs.North], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal([TestCatalogs.North], result.Value.Selectors);
        Assert.Equal("UK OFFICIAL NORTH", result.Value.Label);
        Assert.Equal(["REGION"], context.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id).Select(x => x.Category).ToList());

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        Assert.Equal("page.marking.downgrade", audit.Action);
        Assert.Contains("\"selectors\":{\"REGION\":\"NORTH\"}", audit.DetailsJson);
        Assert.Contains("\"previousSelectors\":{\"FRUIT\":\"APPLE\",\"REGION\":\"NORTH\"}", audit.DetailsJson);
    }

    [Fact]
    public async Task SetMarking_SwappingAValueWithinACategory_UpdatesTheRowInPlace_AndIsADowngrade()
    {
        // APPLE -> BANANA: the row keyed (PageId, FRUIT) is updated, not deleted and
        // re-inserted, and the BANANA readers who could not see the APPLE page now can,
        // which is a downgrade (§21.6).
        using var context = CreateContext();
        var f = Seed(context);
        context.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == f.Page.Id).WithSelectors(TestCatalogs.Apple);
        context.AccessRules.Add(AccessGrantWith(f.Space.Id, TestCatalogs.Apple, TestCatalogs.Banana));
        context.SaveChanges();

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [TestCatalogs.Banana], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        using var readContext = CreateContext();
        var row = Assert.Single(readContext.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id));
        Assert.Equal("BANANA", row.Value);
        Assert.Equal("page.marking.downgrade", Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking"))).Action);
    }

    [Fact]
    public async Task SetMarking_ClearingEverySelector_LeavesNoRows()
    {
        using var context = CreateContext();
        var f = Seed(context);
        context.PageMarkings.Include(m => m.Selectors).Single(m => m.PageId == f.Page.Id).WithSelectors(TestCatalogs.Apple);
        context.AccessRules.Add(AccessGrantWith(f.Space.Id, TestCatalogs.Apple));
        context.SaveChanges();

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [], UkPrefix: true),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Selectors);
        Assert.Equal("UK OFFICIAL", result.Value.Label);
        using var readContext = CreateContext();
        Assert.Empty(readContext.PageMarkingSelectors.Where(x => x.PageId == f.Page.Id));
    }

    // --- The persisted "unknown" state (design.md §21.10) ---------------------------------

    [Fact]
    public async Task SetMarking_OnAnUnavailablePage_IsRefusedForEveryone_NoReMarkAround()
    {
        // A row that says "unknown" reads as FailClosed, and FailClosed fails canEdit
        // for everyone - so re-marking it through the product is refused exactly as
        // reading it is, for the most generous caller there is: a Space-admin whose
        // access grant confers every selector, holding every nationality. There is no
        // role that re-marks around an unknown marking any more than there is one that
        // reads around it (§6.5); the flag is cleared by the origin declaring a marking
        // through sync, or by an operator repairing the row. The service's own write
        // would clear it (it copies IsUnavailable from a Create-built marking, which is
        // always false), which is why it must never get that far here.
        using var context = CreateContext();
        var f = Seed(context, ClassificationLevel.Official);
        context.AccessRules.AddRange(
            AccessGrantWith(f.Space.Id, TestCatalogs.Apple, TestCatalogs.Banana, TestCatalogs.North, TestCatalogs.South),
            Grant(f.Space.Id, SpaceRole.SpaceAdmin));
        context.PageMarkings.Single(m => m.PageId == f.Page.Id).IsUnavailable = true;
        context.SaveChanges();

        var everyone = PrincipalWith(["AUS", "CAN", "NZ", "UK", "US"], ["engineering"]);
        var denied = Assert.IsType<ReadResult<Page>.Denied>(await new PageReadService(context).GetPageAsync(f.Page.Id, everyone));
        Assert.Equal("marking:unavailable", denied.Reason);

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], [], UkPrefix: true),
            everyone, f.ActingUserId, AuditCtx);

        Assert.Equal("canEdit required", Assert.IsType<ForbiddenError>(result.Error).Reason);
        using var readContext = CreateContext();
        Assert.True(readContext.PageMarkings.Single(m => m.PageId == f.Page.Id).IsUnavailable);
    }

    [Fact]
    public async Task SetMarking_WritesAKnownMarking_SoAStatedMarkingCanNeverBeUnavailable()
    {
        // The write side of the flag's contract: a marking set through the product is
        // KNOWN by construction (ProtectiveMarking.Create cannot build the sentinel), and
        // the service writes that fact to the column rather than leaving it to a default.
        using var context = CreateContext();
        var f = Seed(context);

        var result = await MarkingService(context).SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.TopSecret, [], [], UkPrefix: false),
            PrincipalWith(), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess, $"{result.Error}");
        using var readContext = CreateContext();
        var row = readContext.PageMarkings.Include(m => m.Countries).Include(m => m.Selectors).Single(m => m.PageId == f.Page.Id);
        // Byte-for-byte what the old importer wrote for "unknown" - and available.
        Assert.Equal(ClassificationLevel.TopSecret, row.Level);
        Assert.Null(row.Prefix);
        Assert.False(row.IsUnavailable);
        Assert.False(row.ToMarking().IsUnavailable);
        Assert.IsType<ReadResult<Page>.Found>(await new PageReadService(readContext).GetPageAsync(f.Page.Id, PrincipalWith()));
    }

    [Fact]
    public void AnyPageInsertedAlongsideAnUnavailableParent_InheritsTheUnavailability()
    {
        // The persistence-seam backstop copies the flag with the rest of the marking: a
        // child inserted beside a parent whose marking is unknown is unknown too, never
        // an OFFICIAL page reachable beneath a parent nobody can read.
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");
        var child = TestData.NewPage(space, "child", parent);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        var parentRow = TestData.NewMarking(parent, ClassificationLevel.Official);
        parentRow.IsUnavailable = true;
        context.PageMarkings.Add(parentRow);
        context.Pages.Add(child);
        context.SaveChanges();

        using var readContext = CreateContext();
        var childRow = readContext.PageMarkings.Single(m => m.PageId == child.Id);
        Assert.True(childRow.IsUnavailable);
        Assert.True(childRow.ToMarking().IsUnavailable);
    }
}
