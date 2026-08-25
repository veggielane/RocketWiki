using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §21: protective markings as they behave against a real database — the
/// every-page-is-marked invariant, inheritance at creation, the write-side rules on
/// re-marking, and the read paths that must exclude an over-classified page ENTIRELY
/// rather than merely rank it lower or count it.
/// </summary>
public class PageMarkingTests : SqliteTestBase
{
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal PrincipalWith(string? clearance = null, string[]? nationality = null, string[]? groups = null)
    {
        var attributes = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        if (clearance is not null)
        {
            attributes.Add(new("clearance", new[] { clearance }));
        }

        if (nationality is not null)
        {
            attributes.Add(new("nationality", nationality));
        }

        return Principal.Create("user-sub", groups ?? [], attributes);
    }

    private static AccessRule Grant(Guid spaceId, SpaceRole role) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = role,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static AttributeDefinition NationalityRegistry(string? allowedValuesJson = """["GB","US","NZ"]""") => new()
    {
        Key = "nationality",
        ClaimName = "nationality",
        DisplayName = "Nationality",
        Type = AttributeValueType.StringArray,
        AllowedValuesJson = allowedValuesJson,
    };

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
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.Secret, "GB"));
        context.Pages.Add(child);
        context.SaveChanges();

        var childMarking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == child.Id);
        Assert.Equal(ClassificationLevel.Secret, childMarking.Level);
        Assert.Equal(["GB"], childMarking.Countries.Select(c => c.CountryValue));
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
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.Secret, "GB", "US"));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith("SECRET", ["GB"]), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        var marking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == result.Value.Id);
        Assert.Equal(ClassificationLevel.Secret, marking.Level);
        Assert.Equal(["GB", "US"], marking.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreatePage_AtTheRoot_IsOfficial()
    {
        var author = TestData.NewUser();
        var space = TestData.NewSpace();

        using var context = CreateContext();
        context.Users.Add(author);
        context.Spaces.Add(space);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "root", "Root", "# Root"),
            PrincipalWith(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(ClassificationLevel.Official, context.PageMarkings.Single(m => m.PageId == result.Value.Id).Level);
    }

    [Fact]
    public async Task CreatePage_UnderAParentTheCallerCannotBeClearedFor_IsRefused()
    {
        // Creating a page you could not then read is impossible by construction: the
        // create's canEdit check runs against the marking the new page will inherit.
        var space = TestData.NewSpace();
        var parent = TestData.NewPage(space, "parent");

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(parent);
        context.PageMarkings.Add(TestData.NewMarking(parent, ClassificationLevel.TopSecret));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith("SECRET"), Guid.NewGuid(), AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("classification:top_secret", Assert.IsType<ForbiddenError>(result.Error).Reason);
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
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageService(context, "local-instance");
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, parent.Id, "child", "Child", "# Child"),
            PrincipalWith(), author.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("NATO", context.PageMarkings.Single(m => m.PageId == result.Value.Id).Prefix);
    }

    [Fact]
    public async Task SetMarking_NormalizesThePrefix_AndPutsItInTheLabelAndTheAuditRow()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Secret, ["GB"], "  uk  "),
            PrincipalWith("SECRET", ["GB"]), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("UK", result.Value.Prefix);
        Assert.Equal("UK SECRET [GB EYES ONLY]", result.Value.Label);
        Assert.Equal("UK", context.PageMarkings.Single(m => m.PageId == f.Page.Id).Prefix);

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        Assert.Contains("\"prefix\":\"UK\"", audit.DetailsJson);
        Assert.Contains("\"previousPrefix\":\"UK\"", audit.DetailsJson);
    }

    [Fact]
    public async Task SetMarking_ClearingThePrefix_IsPermitted_AndIsNotADowngrade()
    {
        // No prefix is a legal marking, so it must be clearable — and clearing it changes
        // nobody's access, so it stays an ordinary page.marking.set rather than diluting
        // the downgrade query that exists to find real widenings.
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], Prefix: null),
            PrincipalWith("SECRET"), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Prefix);
        Assert.Equal("OFFICIAL", result.Value.Label);
        Assert.Null(context.PageMarkings.Single(m => m.PageId == f.Page.Id).Prefix);
        Assert.Equal("page.marking.set", context.AuditEvents.Single(e => e.Action.StartsWith("page.marking")).Action);
    }

    [Fact]
    public async Task SetMarking_AnOverLongPrefix_IsAValidationError()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, [], new string('X', 17)),
            PrincipalWith("SECRET"), f.ActingUserId, AuditCtx);

        Assert.IsType<ValidationError>(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UK")]
    [InlineData("ZZNONSENSEZZ")]
    public async Task ThePrefix_ChangesNoAccessDecision(string? prefix)
    {
        // The end-to-end half of ClearanceGateTests' invariance proof: the SAME page, read
        // through the real permission loader, gives identical answers to a cleared and an
        // uncleared principal whatever prefix it carries.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarkingWithPrefix(page, ClassificationLevel.Secret, prefix, "GB"));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageReadService(context);

        Assert.IsType<ReadResult<Page>.Found>(
            await service.GetPageAsync(page.Id, PrincipalWith("SECRET", ["GB"])));

        var deniedByLevel = Assert.IsType<ReadResult<Page>.Denied>(
            await service.GetPageAsync(page.Id, PrincipalWith("OFFICIAL", ["GB"])));
        Assert.Equal("classification:secret", deniedByLevel.Reason);

        var deniedByCaveat = Assert.IsType<ReadResult<Page>.Denied>(
            await service.GetPageAsync(page.Id, PrincipalWith("SECRET", ["NZ"])));
        Assert.Equal("caveat:eyes_only", deniedByCaveat.Reason);
    }

    // --- Read paths ----------------------------------------------------------------------

    [Fact]
    public async Task GetPage_OverClassified_IsDeniedWithTheClassificationReason_NotFound()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageReadService(context);
        var denied = Assert.IsType<ReadResult<Page>.Denied>(
            await service.GetPageAsync(page.Id, PrincipalWith("OFFICIAL_SENSITIVE")));

        Assert.Equal("classification:secret", denied.Reason);

        // ... and the same page IS readable by someone cleared for it, proving the denial
        // was the marking and not some unrelated seeding mistake.
        Assert.IsType<ReadResult<Page>.Found>(await service.GetPageAsync(page.Id, PrincipalWith("SECRET")));
    }

    [Fact]
    public async Task GetPage_MissingMarkingRow_IsTreatedAsTopSecret()
    {
        // Belt and braces against a future code path that forgets. The row is removed
        // AFTER the save that materialized it, which is the only way to reach this state.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        context.PageMarkings.Remove(context.PageMarkings.Single(m => m.PageId == page.Id));
        context.SaveChanges();

        var service = new PageReadService(context);

        var denied = Assert.IsType<ReadResult<Page>.Denied>(
            await service.GetPageAsync(page.Id, PrincipalWith("SECRET")));
        Assert.Equal("classification:top_secret", denied.Reason);

        Assert.IsType<ReadResult<Page>.Found>(await service.GetPageAsync(page.Id, PrincipalWith("TOP_SECRET")));
    }

    [Fact]
    public async Task GetPage_EyesOnly_RequiresAMatchingNationality()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "GB", "US"));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageReadService(context);

        Assert.IsType<ReadResult<Page>.Found>(
            await service.GetPageAsync(page.Id, PrincipalWith("SECRET", ["US"])));

        var denied = Assert.IsType<ReadResult<Page>.Denied>(
            await service.GetPageAsync(page.Id, PrincipalWith("SECRET", ["NZ"])));
        Assert.Equal("caveat:eyes_only", denied.Reason);

        // No nationality at all: denied, fail closed like any attr condition.
        Assert.IsType<ReadResult<Page>.Denied>(await service.GetPageAsync(page.Id, PrincipalWith("SECRET")));
    }

    [Fact]
    public async Task PageTree_ExcludesAnOverClassifiedPage_AndItsSubtree_Entirely()
    {
        var space = TestData.NewSpace();
        var open = TestData.NewPage(space, "open");
        var classified = TestData.NewPage(space, "classified");
        var childOfClassified = TestData.NewPage(space, "child", classified);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(open, classified, childOfClassified);
        context.PageMarkings.Add(TestData.NewMarking(classified, ClassificationLevel.TopSecret));
        // The child is deliberately OFFICIAL: it is pruned because its parent is, not
        // because of its own marking - a tree cannot render a node whose parent is absent.
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageReadService(context);
        var found = Assert.IsType<ReadResult<IReadOnlyList<PageTreeNode>>.Found>(
            await service.GetPageTreeAsync(space.Id, PrincipalWith()));

        var node = Assert.Single(found.Value);
        Assert.Equal(open.Id, node.Id);
        // Not "one node with a placeholder" and not a count that implies a hidden sibling:
        // the classified branch is absent entirely (§6.7).
        Assert.Empty(node.Children);
    }

    [Fact]
    public async Task PageTree_CarriesEachVisibleNodesMarking_TheSameValueThePruningUsed()
    {
        // The tree is a listing surface, and every node in it already passed the clearance
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
        context.PageMarkings.Add(TestData.NewMarkingWithPrefix(child, ClassificationLevel.Secret, "NATO", "GB", "US"));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.SaveChanges();

        var service = new PageReadService(context);
        var found = Assert.IsType<ReadResult<IReadOnlyList<PageTreeNode>>.Found>(
            await service.GetPageTreeAsync(space.Id, PrincipalWith("SECRET", ["GB"])));

        var rootNode = Assert.Single(found.Value);
        Assert.Equal("UK OFFICIAL", rootNode.Marking.Label);
        Assert.Equal(ClassificationLevel.Official, rootNode.Marking.Level);

        var childNode = Assert.Single(rootNode.Children);
        Assert.Equal("NATO SECRET [GB/US EYES ONLY]", childNode.Marking.Label);
        Assert.Equal(["GB", "US"], childNode.Marking.EyesOnly);
    }

    [Fact]
    public async Task PageTree_APageWhoseMarkingRowIsMissing_IsPruned_AndNeverReportsABlankMarking()
    {
        // The fail-closed guard reaching the listing surface: a page with no marking row
        // reads as TOP SECRET, so it is pruned for anyone below that - and for a caller
        // who IS cleared, the node reports TOP SECRET rather than an empty badge that
        // would misrepresent the enforcement they are subject to.
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space, "orphaned");

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        context.PageMarkings.Remove(context.PageMarkings.Single(m => m.PageId == page.Id));
        context.SaveChanges();

        var service = new PageReadService(context);

        var pruned = Assert.IsType<ReadResult<IReadOnlyList<PageTreeNode>>.Found>(
            await service.GetPageTreeAsync(space.Id, PrincipalWith("SECRET")));
        Assert.Empty(pruned.Value);

        var visible = Assert.IsType<ReadResult<IReadOnlyList<PageTreeNode>>.Found>(
            await service.GetPageTreeAsync(space.Id, PrincipalWith("TOP_SECRET")));
        Assert.Equal("TOP SECRET", Assert.Single(visible.Value).Marking.Label);
    }

    [Fact]
    public async Task Search_ExcludesAnOverClassifiedPage_TitleAndSnippetNeverBuilt()
    {
        var space = TestData.NewSpace();
        var open = TestData.NewPage(space, "open");
        open.Title = "Nozzle geometry";
        open.CurrentContent = "# Nozzle geometry\n\nExpansion ratio notes.";
        var classified = TestData.NewPage(space, "classified");
        classified.Title = "Nozzle geometry ZZSECRETTITLEZZ";
        classified.CurrentContent = "# Nozzle geometry\n\nZZSECRETBODYZZ expansion ratio.";

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.AddRange(open, classified);
        context.PageMarkings.Add(TestData.NewMarking(classified, ClassificationLevel.Secret));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
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

    private Fixture Seed(RocketWikiDbContext context, ClassificationLevel level = ClassificationLevel.Official,
        string? allowedValuesJson = """["GB","US","NZ"]""")
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        context.Users.Add(user);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, level));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.AttributeDefinitions.Add(NationalityRegistry(allowedValuesJson));
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
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Secret, ["gb", "US"]),
            PrincipalWith("TOP_SECRET", ["GB"]), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal("UK SECRET [GB/US EYES ONLY]", result.Value.Label);

        var marking = context.PageMarkings.Include(m => m.Countries).Single(m => m.PageId == f.Page.Id);
        Assert.Equal(ClassificationLevel.Secret, marking.Level);
        Assert.Equal(["GB", "US"], marking.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal));
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
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, []),
            PrincipalWith("SECRET"), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);

        var audit = Assert.Single(context.AuditEvents.Where(e => e.Action.StartsWith("page.marking")));
        // Its own action name, so a reviewer can find every widening with one query.
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
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "GB"));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.AttributeDefinitions.Add(NationalityRegistry());
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, []),
            PrincipalWith("SECRET", ["GB"]), user.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Empty(context.PageMarkingCountries.Where(c => c.PageId == page.Id));
        Assert.Equal("page.marking.downgrade", context.AuditEvents.Single(e => e.Action.StartsWith("page.marking")).Action);
    }

    [Fact]
    public async Task SetMarking_AboveYourOwnClearance_IsForbidden()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.TopSecret, []),
            PrincipalWith("SECRET"), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("classification:top_secret", Assert.IsType<ForbiddenError>(result.Error).Reason);
        Assert.Equal(ClassificationLevel.Official, context.PageMarkings.Single(m => m.PageId == f.Page.Id).Level);
    }

    [Fact]
    public async Task SetMarking_ToAnEyesOnlySetYouAreNotIn_IsForbidden()
    {
        // Same rule as the level, for the same stated reason: you may not classify a page
        // out of your own reach. A GB editor marking a page [US EYES ONLY] loses it as
        // completely as over-classifying it would.
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, ["US"]),
            PrincipalWith("SECRET", ["GB"]), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Equal("caveat:eyes_only", Assert.IsType<ForbiddenError>(result.Error).Reason);
    }

    [Fact]
    public async Task SetMarking_ACountryOutsideTheRegisteredNationalityVocabulary_IsRefused()
    {
        using var context = CreateContext();
        var f = Seed(context);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, ["FR"]),
            PrincipalWith("SECRET", ["FR"]), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Contains("FR", error.Message, StringComparison.Ordinal);
        Assert.Contains("nationality", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task SetMarking_WithNoRegisteredNationalityVocabulary_RefusesAnEyesOnlySetAltogether(string? allowedValuesJson)
    {
        // §21: there is nothing to pick from, and a caveat naming values the instance does
        // not recognise would match nobody - a page released to no one, silently.
        using var context = CreateContext();
        var f = Seed(context, allowedValuesJson: allowedValuesJson);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, ["GB"]),
            PrincipalWith("SECRET", ["GB"]), f.ActingUserId, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Contains("eyes-only", Assert.IsType<ValidationError>(result.Error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetMarking_WithNoRegisteredNationalityVocabulary_StillAllowsALevelOnlyMarking()
    {
        // An instance with no nationality attribute is a perfectly reasonable instance;
        // markings must still work there.
        using var context = CreateContext();
        var f = Seed(context, allowedValuesJson: null);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Secret, []),
            PrincipalWith("SECRET"), f.ActingUserId, AuditCtx);

        Assert.True(result.IsSuccess);
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
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Viewer));
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, []),
            PrincipalWith("TOP_SECRET"), user.Id, AuditCtx);

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
        context.AccessRules.Add(Grant(space.Id, SpaceRole.SpaceAdmin));
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, []),
            PrincipalWith("TOP_SECRET"), user.Id, AuditCtx);

        Assert.IsType<ReadOnlyReplicaError>(result.Error);
    }

    [Fact]
    public async Task SetMarking_OnAPageYouCannotSee_IsRefusedWithoutRevealingItExists()
    {
        // canEdit already includes the clearance gate against the page's CURRENT marking,
        // so a page you cannot read is a page you cannot re-mark - including re-marking it
        // downward to make it readable.
        using var context = CreateContext();
        var f = Seed(context, ClassificationLevel.TopSecret);

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(f.Page.Id, ClassificationLevel.Official, []),
            PrincipalWith("OFFICIAL"), f.ActingUserId, AuditCtx);

        Assert.Equal("canEdit required", Assert.IsType<ForbiddenError>(result.Error).Reason);
        Assert.Equal(ClassificationLevel.TopSecret, context.PageMarkings.Single(m => m.PageId == f.Page.Id).Level);
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
        context.PageMarkings.Add(TestData.NewMarking(page, ClassificationLevel.Secret, "GB", "US"));
        context.AccessRules.Add(Grant(space.Id, SpaceRole.Editor));
        context.AttributeDefinitions.Add(NationalityRegistry());
        context.SaveChanges();

        var service = new PageMarkingService(context, "local-instance");
        var result = await service.SetAsync(
            new SetPageMarkingRequest(page.Id, ClassificationLevel.Secret, ["NZ", "GB"]),
            PrincipalWith("SECRET", ["GB"]), user.Id, AuditCtx);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["GB", "NZ"],
            context.PageMarkingCountries.Where(c => c.PageId == page.Id)
                .Select(c => c.CountryValue).ToList().OrderBy(c => c, StringComparer.Ordinal));
    }
}
