using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Case-insensitive URLs at the HTTP boundary. <c>/spaces/{key}/{slug}</c> is an
/// address, so every casing of it names the same thing — <c>pageBySlug</c> and
/// <c>space</c> both, since a URL has two halves and folding only one would be a
/// half-working feature.
///
/// <para>The mechanism is canonical stored forms plus normalized lookups, not a
/// case-insensitive collation: the BIN2 collation (data-model.md) is what keeps the two
/// providers agreeing about "same key", and this layer is what makes URLs forgiving on
/// top of it. Normalizing lookups alone would be ambiguous (two stored casings, one
/// URL); canonical storage alone would 404 the casing the user typed.</para>
///
/// <para>The third test is the one that must not be lost: making an address forgiving
/// must not make a DENIED address distinguishable from a nonexistent one (§6.7).</para>
/// </summary>
public sealed class CaseInsensitiveAddressTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(string SpaceKey, Guid OpenPageId, Guid RestrictedPageId);

    /// <summary>
    /// A space whose key is written in mixed case, holding an open page and one behind a
    /// view restriction nobody in these tests satisfies.
    /// </summary>
    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"case-{Guid.NewGuid()}", DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var rawKey = $"cA{Guid.NewGuid():N}"[..8]; // deliberately NOT canonical
        var space = new Space
        {
            Key = rawKey, Name = "Case Space", OriginInstanceId = "standalone",
            CreatedAtUtc = now, CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant, SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
        });

        var open = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "Open-Page", Title = "Open page",
            CurrentContent = "# Open", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var restricted = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "Hidden-Page", Title = "Hidden page",
            CurrentContent = "# Hidden", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.AddRange(open, restricted);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction, PageId = restricted.Id, Action = PageAction.View,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition("nobody-is-in-this")),
            CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
        });
        await db.SaveChangesAsync();

        // The seed wrote mixed case; the persistence seam canonicalized both halves.
        Assert.Equal(rawKey.ToUpperInvariant(), space.Key);
        Assert.Equal("open-page", open.Slug);

        return new Fixture(space.Key, open.Id, restricted.Id);
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"case-{Guid.NewGuid()}");
        return client;
    }

    [Fact]
    public async Task PageBySlug_ResolvesTheSamePage_WhicheverCaseEitherHalfIsTypedIn()
    {
        var f = await SeedAsync();
        var client = Client();

        // The canonical address, and three casings a person might actually type or paste.
        foreach (var (key, slug) in new[]
        {
            (f.SpaceKey, "open-page"),
            (f.SpaceKey.ToLowerInvariant(), "open-page"),
            (f.SpaceKey, "OPEN-PAGE"),
            (f.SpaceKey.ToLowerInvariant(), "Open-Page"),
        })
        {
            using var result = await client.PostGraphQLAsync($$"""
                { pageBySlug(spaceKey: "{{key}}", slug: "{{slug}}") { id slug } }
                """);

            var page = result.RootElement.GetProperty("data").GetProperty("pageBySlug");
            Assert.Equal(f.OpenPageId.ToString(), page.GetProperty("id").GetString());
            // And the slug it reports is the canonical one, not the casing that was asked
            // for — a page has one address, however you reached it.
            Assert.Equal("open-page", page.GetProperty("slug").GetString());
        }
    }

    [Fact]
    public async Task Space_ResolvesByKeyInAnyCase()
    {
        var f = await SeedAsync();
        var client = Client();

        foreach (var key in new[] { f.SpaceKey, f.SpaceKey.ToLowerInvariant() })
        {
            using var result = await client.PostGraphQLAsync($$"""{ space(key: "{{key}}") { key name } }""");
            var space = result.RootElement.GetProperty("data").GetProperty("space");
            Assert.Equal(f.SpaceKey, space.GetProperty("key").GetString());
        }
    }

    /// <summary>
    /// design.md §6.7: a page the caller may not view is byte-identical to one that does
    /// not exist. Case-insensitive addressing widens how many strings reach a lookup, so
    /// it also widens how many ways that invariant could be broken — a wrong-case
    /// denial that answered differently from a wrong-case miss would be an existence
    /// oracle with an extra step.
    /// </summary>
    [Fact]
    public async Task AWrongCaseAddressForAPageTheCallerCannotView_IsByteIdenticalToNotFound()
    {
        var f = await SeedAsync();
        var client = Client();

        async Task<string> RawAsync(string key, string slug)
        {
            using var result = await client.PostGraphQLAsync($$"""
                { pageBySlug(spaceKey: "{{key}}", slug: "{{slug}}") { id title } }
                """);
            return result.RootElement.GetRawText();
        }

        var lowerKey = f.SpaceKey.ToLowerInvariant();

        // Denied, addressed in the canonical case and in two wrong ones.
        var deniedCanonical = await RawAsync(f.SpaceKey, "hidden-page");
        var deniedWrongSlugCase = await RawAsync(f.SpaceKey, "HIDDEN-PAGE");
        var deniedWrongKeyCase = await RawAsync(lowerKey, "Hidden-Page");

        // A slug that genuinely does not exist, and a space key that does not either.
        var missingSlug = await RawAsync(f.SpaceKey, "no-such-page");
        var missingSpace = await RawAsync("ZZNOSUCHZZ", "hidden-page");

        Assert.Equal(missingSlug, deniedCanonical);
        Assert.Equal(missingSlug, deniedWrongSlugCase);
        Assert.Equal(missingSlug, deniedWrongKeyCase);
        Assert.Equal(missingSlug, missingSpace);

        // Non-vacuous: the same query for the page the caller CAN see is different.
        Assert.NotEqual(missingSlug, await RawAsync(lowerKey, "OPEN-PAGE"));
    }
}
