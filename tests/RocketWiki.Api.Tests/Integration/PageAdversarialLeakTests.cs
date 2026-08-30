using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.7's central guarantee, tested adversarially: a restricted page must be
/// absent through every path a Page is reachable by, not merely inaccessible at the
/// obvious one. Fixture:
///
/// <code>
/// Space (everyone: viewer)
/// └── PageA (permitted)
///     ├── PageB (RESTRICTED - view requires nationality US; our principal is NZ)
///     │   └── PageC (inherits PageB's restriction - design.md §6.4 accumulation)
///     └── PageD (permitted sibling of PageB)
/// </code>
///
/// If any assertion here fails, that is the most valuable thing this test suite could
/// find - a real leak, not a false negative.
/// </summary>
public sealed class PageAdversarialLeakTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(Guid SpaceId, Guid PageAId, Guid PageBId, Guid PageCId, Guid PageDId);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"ADV{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Adversarial Test Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        var now = DateTime.UtcNow;
        var pageA = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "a", Title = "Page A", CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.Add(pageA);
        await db.SaveChangesAsync();

        var pageB = new Page
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = "b-restricted", Title = "Page B (restricted)", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var pageD = new Page
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = "d", Title = "Page D", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.AddRange(pageB, pageD);
        await db.SaveChangesAsync();

        var pageC = new Page
        {
            SpaceId = space.Id, ParentPageId = pageB.Id, AncestorPath = $"/{pageA.Id}/{pageB.Id}/",
            Slug = "c", Title = "Page C (inherits restriction)", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.Add(pageC);
        await db.SaveChangesAsync();

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            PageId = pageB.Id,
            Action = PageAction.View,
            ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });
        await db.SaveChangesAsync();

        return new Fixture(space.Id, pageA.Id, pageB.Id, pageC.Id, pageD.Id);
    }

    private static HttpClient NzClient(RocketWikiApiFactory factory)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", groups: [], nationality: ["NZ"]);
        return client;
    }

    [Fact]
    public async Task DirectQuery_OfRestrictedPage_IsAbsent()
    {
        var f = await SeedAsync();
        var client = NzClient(factory);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageBId}}") { id title } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
    }

    [Fact]
    public async Task AsChildOfPermittedParent_RestrictedPageIsAbsent_PermittedSiblingIsPresent()
    {
        var f = await SeedAsync();
        var client = NzClient(factory);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageAId}}") { children { id } } }""");

        var childIds = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("children")
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();

        Assert.DoesNotContain(f.PageBId.ToString(), childIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(childIds, id => string.Equals(id, f.PageDId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InheritedRestriction_ChildOfRestrictedPage_IsAlsoAbsentDirectly()
    {
        // design.md §6.4: restrictions accumulate down the tree. PageC carries no
        // restriction of its own - it's unreachable purely because its ancestor PageB is.
        var f = await SeedAsync();
        var client = NzClient(factory);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageCId}}") { id } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
    }

    [Fact]
    public async Task ViaRevisionHistory_RestrictedPagesContentNeverLeaks()
    {
        // page(id) already returns null for PageB, so there is no object to even ask
        // for .revisions on - this confirms the whole field selection comes back empty,
        // not merely the top-level page reference.
        var f = await SeedAsync();
        var client = NzClient(factory);

        var result = await client.PostGraphQLAsync(
            $$"""{ page(id: "{{f.PageBId}}") { id content revisions { id content } } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        Assert.False(result.RootElement.TryGetProperty("errors", out _),
            "Selecting fields on a null page must not surface an error that could itself hint the page exists.");
    }

    [Fact]
    public async Task ViaTreeTraversal_RestrictedSubtreeIsEntirelyAbsent()
    {
        var f = await SeedAsync();
        var client = NzClient(factory);

        var result = await client.PostGraphQLAsync(
            $$"""{ pageTree(spaceId: "{{f.SpaceId}}") { id children { id children { id } } } }""");

        var json = result.RootElement.GetProperty("data").GetProperty("pageTree").ToString();
        Assert.DoesNotContain(f.PageBId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.PageCId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(f.PageAId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(f.PageDId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AsParentOfAPermittedChild_TraversalUpwardsIsCorrectAndNeverLeaksTheRestrictedSibling()
    {
        // The positive-path complement to the above: PageD (permitted) correctly resolves
        // its real, permitted parent PageA - proving parent-traversal isn't just failing
        // safe by accident (e.g. always returning null), and that resolving a permitted
        // page's parent never somehow surfaces its restricted sibling PageB instead.
        var f = await SeedAsync();
        var client = NzClient(factory);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageDId}}") { parent { id } } }""");

        var parentId = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("parent").GetProperty("id").GetString();
        Assert.Equal(f.PageAId.ToString(), parentId, ignoreCase: true);
    }

    [Fact]
    public async Task PrincipalSatisfyingTheRestriction_CanSeeTheOtherwiseRestrictedPage()
    {
        // Positive control: proves the NZ-principal failures above are the restriction
        // actually working, not the resolver wiring being broken for PageB in general.
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"us-{Guid.NewGuid()}", nationality: ["US"]);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageBId}}") { id } }""");

        Assert.Equal(f.PageBId.ToString(), result.RootElement.GetProperty("data").GetProperty("page").GetProperty("id").GetString(), ignoreCase: true);
    }
}
