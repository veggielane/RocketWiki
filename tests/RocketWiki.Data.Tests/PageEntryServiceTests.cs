using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Tests.Access;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Entries: structured objects on a page, each with its own marking.
///
/// <para>The ordinary CRUD is here, but the tests that matter are the ones about the
/// invariant this feature CHANGES. Until now a page you could see was a page whose every
/// part you could see; entries make that false, and the cost of getting it wrong is a
/// disclosure rather than a bug. So: a pruned entry is indistinguishable from an absent
/// one, an entry cannot be marked below its page, and you cannot mark one out of your own
/// reach.</para>
/// </summary>
public class PageEntryServiceTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-1", "127.0.0.1");

    private static Principal Caller() => Principal.Create("caller-sub", []);

    /// <summary>A principal holding a nationality — the one principal-side fact an entry's
    /// marking is compared against (the eyes-only caveat, §21.4). The plain
    /// <see cref="Caller"/> holds none, so a UK EYES ONLY entry is out of its reach; the
    /// level alone puts nothing out of anyone's reach.</summary>
    private static Principal NationalCaller(params string[] nationality) =>
        Principal.Create("caller-sub", [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["nationality"] = nationality,
        });

    private async Task<(RocketWikiDbContext Context, Page Page, User Actor)> SeedAsync(
        ClassificationLevel pageLevel = ClassificationLevel.Official)
    {
        var actor = TestData.NewUser();
        var space = TestData.NewSpace();
        var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        await context.SaveChangesAsync();

        var grant = await new AccessRuleService(context).CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.RoleGrant, space.Id, null, SpaceRole.Editor, null, """{ "everyone": true }"""),
            Principal.Create("bootstrap", []), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.True(grant.IsSuccess, $"grant failed: {grant.Error}");
        // A role confers no visibility (design.md §6.4); the access grant sits beside it.
        var access = await new AccessRuleService(context).CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.AccessGrant, space.Id, null, null, null, """{ "everyone": true }"""),
            Principal.Create("bootstrap", []), isInstanceAdmin: true, actor.Id, AuditCtx);
        Assert.True(access.IsSuccess, $"access grant failed: {access.Error}");

        var page = TestData.NewPage(space, "runbook");
        context.Pages.Add(page);
        context.PageMarkings.Add(TestData.NewMarking(page, pageLevel));
        context.SaveChanges();
        return (context, page, actor);
    }

    private static PageEntryService NewService(RocketWikiDbContext context) => new(context, LocalInstanceId);

    [Fact]
    public async Task Create_ThenList_RoundTripsTheObject()
    {
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);

        var created = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "incident-report", """{"severity":"high"}"""),
            Caller(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess, $"{created.Error}");

        var listed = await service.ListAsync(page.Id, "incident-report", Caller());
        var entries = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(listed).Value;
        Assert.Equal("""{"severity":"high"}""", Assert.Single(entries).Data);
        Assert.Equal(1, entries[0].Version);
    }

    [Fact]
    public async Task Create_NormalizesTheCollection_SoCaseCannotSplitOneSetInTwo()
    {
        // The collation trap, at the service boundary. SQL Server would treat these as one
        // collection and SQLite as two, so normalizing on write is what makes the feature
        // behave the same on both.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);

        foreach (var collection in new[] { "Incident-Report", "incident-report", "  INCIDENT-REPORT  " })
        {
            Assert.True((await service.CreateAsync(
                new CreatePageEntryRequest(page.Id, collection, "{}"), Caller(), actor.Id, AuditCtx)).IsSuccess);
        }

        var listed = await service.ListAsync(page.Id, "incident-report", Caller());
        Assert.Equal(3, Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(listed).Value.Count);
    }

    [Fact]
    public async Task List_ReturnsSeveralCollectionsIndependently()
    {
        // A page holds any number of collections; one collection's entries never leak into
        // another's listing.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);

        await service.CreateAsync(new CreatePageEntryRequest(page.Id, "incident-report", "{}"), Caller(), actor.Id, AuditCtx);
        await service.CreateAsync(new CreatePageEntryRequest(page.Id, "action-item", "{}"), Caller(), actor.Id, AuditCtx);
        await service.CreateAsync(new CreatePageEntryRequest(page.Id, "action-item", "{}"), Caller(), actor.Id, AuditCtx);

        var incidents = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "incident-report", Caller())).Value;
        var actions = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "action-item", Caller())).Value;

        Assert.Single(incidents);
        Assert.Equal(2, actions.Count);
    }

    [Fact]
    public async Task List_PrunesEntriesOutOfTheCallersReach_AndSaysNothingAboutThem()
    {
        // THE test. A fully-pruned collection must be indistinguishable from an empty one:
        // no count, no total, no gap. A number that moved when caveated entries existed
        // would report their existence to someone §21 has already decided must not learn it.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);
        var cleared = NationalCaller("UK");

        await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", """{"n":1}"""), Caller(), actor.Id, AuditCtx);
        for (var i = 0; i < 5; i++)
        {
            var secret = await service.CreateAsync(
                new CreatePageEntryRequest(page.Id, "notes", $$"""{"n":{{i}}}""",
                    ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: "UK")),
                cleared, actor.Id, AuditCtx);
            Assert.True(secret.IsSuccess, $"{secret.Error}");
        }

        // A caller with no nationality sees exactly the one entry they may read.
        var official = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "notes", Caller())).Value;
        Assert.Equal("""{"n":1}""", Assert.Single(official).Data);

        // And the cleared caller sees all six — same call, same page, different answer,
        // which is the gate working rather than an inconsistency.
        var all = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "notes", cleared)).Value;
        Assert.Equal(6, all.Count);
    }

    [Fact]
    public async Task List_OfAFullyPrunedCollection_IsIdenticalToAnEmptyOne()
    {
        // The §6.7 property stated directly: the two results must be byte-identical, so
        // no caller can tell "nothing here" from "nothing here for you".
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);

        await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "classified", "{}",
                ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: "UK")),
            NationalCaller("UK"), actor.Id, AuditCtx);

        var pruned = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "classified", Caller())).Value;
        var absent = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "never-used", Caller())).Value;

        Assert.Empty(pruned);
        Assert.Empty(absent);
    }

    [Fact]
    public async Task Get_OfAnEntryOutOfTheCallersReach_IsDeniedNotReturned()
    {
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);

        var created = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}",
                ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: "UK")),
            NationalCaller("UK"), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);

        var read = await service.GetAsync(created.Value.Id, Caller());
        Assert.IsType<ReadResult<PageEntryView>.Denied>(read);
    }

    [Fact]
    public async Task Create_WithAMarkingBelowThePage_IsRefused()
    {
        // The page is the container. A reader who cannot open a SECRET page never reaches
        // its entries, so an OFFICIAL entry inside one is a claim that will eventually be
        // believed by someone.
        var (context, page, actor) = await SeedAsync(ClassificationLevel.Secret);
        using var _ = context;
        var service = NewService(context);

        var result = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}",
                ProtectiveMarking.Create(ClassificationLevel.Official, [], prefix: "UK")),
            NationalCaller("UK"), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task Create_WithAMarkingTheCallerCouldNotThenRead_IsRefused()
    {
        // §21.6's rule, applied per entry: it also stops someone writing a record they
        // can never afterwards correct. The caller holds no nationality, so a US EYES
        // ONLY entry would be out of their reach; the level alone never is (§21.12), so
        // the same caller may file a TOP SECRET entry with no caveat.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;

        var result = await NewService(context).CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}",
                ProtectiveMarking.Create(ClassificationLevel.Official, ["US"], prefix: "UK")),
            Caller(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ForbiddenError>(result.Error);

        var topSecret = await NewService(context).CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}",
                ProtectiveMarking.Create(ClassificationLevel.TopSecret, [], prefix: "UK")),
            Caller(), actor.Id, AuditCtx);

        Assert.True(topSecret.IsSuccess, $"{topSecret.Error}");
    }

    [Fact]
    public async Task Create_InheritsThePagesMarkingWhenNoneIsGiven()
    {
        // The ordinary case: a form submitter should not have to reason about
        // classification to file a record.
        var (context, page, actor) = await SeedAsync(ClassificationLevel.Secret);
        using var _ = context;

        var created = await NewService(context).CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}"), NationalCaller("UK"), actor.Id, AuditCtx);

        Assert.True(created.IsSuccess, $"{created.Error}");
        Assert.Equal(ClassificationLevel.Secret, created.Value.Marking.Level);
    }

    [Fact]
    public async Task Update_WithAStaleVersion_IsRefused()
    {
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);
        var created = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", """{"v":1}"""), Caller(), actor.Id, AuditCtx);

        var first = await service.UpdateAsync(
            new UpdatePageEntryRequest(created.Value.Id, 1, """{"v":2}"""), Caller(), actor.Id, AuditCtx);
        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value.Version);

        // A second writer holding the version they read before the first write.
        var stale = await service.UpdateAsync(
            new UpdatePageEntryRequest(created.Value.Id, 1, """{"v":3}"""), Caller(), actor.Id, AuditCtx);
        Assert.False(stale.IsSuccess);
        Assert.IsType<StaleRevisionError>(stale.Error);
    }

    [Fact]
    public async Task Update_OfAnEntryOutOfTheCallersReach_LooksLikeItDoesNotExist()
    {
        // Not a Forbidden: a distinguishable refusal would let a caller discover that an
        // entry exists at an id, and — because the marking is checked before the version —
        // would also leak its version through which error came back.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);
        var created = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}",
                ProtectiveMarking.Create(ClassificationLevel.Secret, ["UK"], prefix: "UK")),
            NationalCaller("UK"), actor.Id, AuditCtx);

        var result = await service.UpdateAsync(
            new UpdatePageEntryRequest(created.Value.Id, 1, """{"x":1}"""), Caller(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task Delete_SoftDeletes_AndTheEntryLeavesEveryListing()
    {
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);
        var created = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}"), Caller(), actor.Id, AuditCtx);

        Assert.True((await service.DeleteAsync(
            new DeletePageEntryRequest(created.Value.Id, 1), Caller(), actor.Id, AuditCtx)).IsSuccess);

        var listed = Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "notes", Caller())).Value;
        Assert.Empty(listed);

        // The row survives, which is what lets it sync and be restored.
        Assert.True(await context.PageEntries.IgnoreQueryFilters().AnyAsync(e => e.Id == created.Value.Id));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task Create_WithSomethingThatIsNotAJsonObject_IsRefused(string data)
    {
        // The storage contract is the server's to keep: every consumer downstream reads
        // an entry as an object, and a bare scalar would only fail at the far end.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;

        var result = await NewService(context).CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", data), Caller(), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.IsType<ValidationError>(result.Error);
    }

    [Fact]
    public async Task List_ForAPageTheCallerCannotView_IsRefusedBeforeAnyEntryIsConsidered()
    {
        var (context, page, _) = await SeedAsync();
        using var _c = context;
        // A principal with no space role at all.
        var stranger = Principal.Create("stranger-sub", []);
        await context.AccessRules.ExecuteDeleteAsync();

        var result = await NewService(context).ListAsync(page.Id, "notes", stranger);
        Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Denied>(result);
    }

    [Fact]
    public async Task Create_WithAMarkingCarryingSelectors_IsRefused_UntilEntriesCanStoreThem()
    {
        // design.md §21.14: entries carry no selector storage this round. A selector the
        // row cannot hold is refused, never silently dropped into a wider marking.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;

        var result = await NewService(context).CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", "{}",
                ProtectiveMarking.Create(ClassificationLevel.Official, [], [TestCatalogs.Apple], "UK")),
            NationalCaller("UK"), actor.Id, AuditCtx);

        Assert.False(result.IsSuccess);
        Assert.Contains("selectors", Assert.IsType<ValidationError>(result.Error).Message, StringComparison.Ordinal);
        Assert.Empty(context.PageEntries.ToList());
    }

    [Fact]
    public async Task AnUnavailableEntry_IsDeniedToEveryone_PrunedFromListings_AndLooksAbsentToAnUpdate()
    {
        // design.md §21.10: an entry whose row says "unknown" (the sync importer's write
        // for a payload carrying no marking) reads as FailClosed on every path, for the
        // most generous caller there is - its TOP SECRET level is the sentinel's
        // rendering, not what denies. The page itself stays readable.
        var (context, page, actor) = await SeedAsync();
        using var _ = context;
        var service = NewService(context);
        var created = await service.CreateAsync(
            new CreatePageEntryRequest(page.Id, "notes", """{"n":1}"""), Caller(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess, $"{created.Error}");
        context.PageEntries.Single(e => e.Id == created.Value.Id).IsUnavailable = true;
        await context.SaveChangesAsync();

        var everyone = Principal.Create("caller-sub", ["engineering"], new Dictionary<string, IReadOnlyList<string>>
        {
            ["nationality"] = ["AUS", "CAN", "NZ", "UK", "US"],
        });

        var denied = Assert.IsType<ReadResult<PageEntryView>.Denied>(await service.GetAsync(created.Value.Id, everyone));
        Assert.Equal("marking:unavailable", denied.Reason);
        Assert.Empty(Assert.IsType<ReadResult<IReadOnlyList<PageEntryView>>.Found>(
            await service.ListAsync(page.Id, "notes", everyone)).Value);

        var update = await service.UpdateAsync(
            new UpdatePageEntryRequest(created.Value.Id, 1, """{"n":2}"""), everyone, actor.Id, AuditCtx);
        Assert.IsType<NotFoundError>(update.Error);
        Assert.True(context.PageEntries.Single(e => e.Id == created.Value.Id).IsUnavailable);
    }
}
