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
/// The <c>pageQuery</c> root field end to end (design.md §22, and §6.7/§7/§21 for what it
/// must not do): boolean semantics against seeded data, the stated cap, ordering and
/// pagination, the audit row — and, above all, the absence properties.
///
/// <para>The test that matters most is
/// <see cref="InvisibleSpace_NonexistentSpace_UnknownLabel_AndUnknownCreator_AreByteIdentical"/>.
/// design.md §6.7 requires a space the caller holds no role in to be indistinguishable from
/// one that never existed, and a query language is the easiest place in the system to break
/// that with a well-meaning "no such space" error. It is asserted on the raw response bytes
/// rather than on a shape, because that is the level the guarantee is actually made at.</para>
///
/// <para>The factory is shared per class and <c>pageQuery</c> scans every space the caller
/// holds a role in, so every test seeds its own space and scopes its query to that key —
/// otherwise another test's <c>everyone</c>-granted space would leak into the results.</para>
/// </summary>
public sealed class PageQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    // ---------- seeding ----------

    private async Task<(Space Space, User Creator)> SeedSpaceAsync(string? grantExpressionJson = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"rqlseed-{Guid.NewGuid():N}",
            DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);

        var space = new Space
        {
            Key = $"R{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "RQL Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = grantExpressionJson ?? RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        await db.SaveChangesAsync();
        return (space, creator);
    }

    private async Task<Page> SeedPageAsync(
        Space space,
        string slug,
        string title,
        string? labelName = null,
        DateTime? createdAtUtc = null,
        DateTime? updatedAtUtc = null,
        string? restrictToGroup = null,
        ClassificationLevel level = ClassificationLevel.Official,
        Guid? authorUserId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = slug,
            Title = title,
            CurrentContent = $"content for {slug}",
            CurrentRevisionNumber = 1,
            CreatedAtUtc = createdAtUtc ?? now,
            UpdatedAtUtc = updatedAtUtc ?? now,
        };
        db.Pages.Add(page);

        if (level != ClassificationLevel.Official)
        {
            // Seeded directly rather than through setPageMarking so a test can build a
            // corpus a single editor could never assemble (design.md §21.6).
            db.PageMarkings.Add(new PageMarking
            {
                PageId = page.Id, Level = level, Prefix = ProtectiveMarking.DefaultPrefix, SetAtUtc = now,
            });
        }

        if (authorUserId is not null)
        {
            db.PageRevisions.Add(new PageRevision
            {
                PageId = page.Id,
                RevisionNumber = 1,
                Title = title,
                Content = page.CurrentContent,
                AuthorUserId = authorUserId.Value,
                CreatedAtUtc = page.CreatedAtUtc,
            });
        }

        if (labelName is not null)
        {
            var label = await db.Labels.FirstOrDefaultAsync(l => l.SpaceId == space.Id && l.Name == labelName);
            if (label is null)
            {
                label = new Label { SpaceId = space.Id, Name = labelName };
                db.Labels.Add(label);
            }

            db.PageLabels.Add(new PageLabel { PageId = page.Id, LabelId = label.Id });
        }

        if (restrictToGroup is not null)
        {
            db.AccessRules.Add(new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction,
                PageId = page.Id,
                Action = PageAction.View,
                ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition(restrictToGroup)),
                CreatedAtUtc = now,
                CreatedByUserId = space.CreatedByUserId,
                UpdatedAtUtc = now,
                UpdatedByUserId = space.CreatedByUserId,
            });
        }

        await db.SaveChangesAsync();
        return page;
    }

    private HttpClient CreateUserClient(string? sub = null, string[]? groups = null, string? clearance = null)
    {
        var client = factory.CreateClient();
        client.SetTestUser(
            sub: sub ?? $"rql-{Guid.NewGuid():N}", name: "RQL Tester", groups: groups, clearance: clearance);
        return client;
    }

    // ---------- helpers ----------

    private const string ConnectionSelection =
        "totalCount pageInfo { hasNextPage endCursor } edges { cursor node { page { id title } } } "
        + "errors { code message offset length } aggregateMarking { label }";

    private static async Task<JsonDocument> RunAsync(HttpClient client, string rql) =>
        await client.PostGraphQLAsync($$"""
            query { pageQuery(query: "{{rql}}") { {{ConnectionSelection}} } }
            """);

    private static JsonElement Connection(JsonDocument response) =>
        response.RootElement.GetProperty("data").GetProperty("pageQuery");

    private static string[] Titles(JsonElement connection) =>
        connection.GetProperty("edges").EnumerateArray()
            .Select(e => e.GetProperty("node").GetProperty("page").GetProperty("title").GetString()!)
            .ToArray();

    // ---------- contract and semantics ----------

    [Fact]
    public async Task PageQuery_ReturnsTheConnectionShape_WithPageResolvedThroughTheAuthorizedLoader()
    {
        var (space, _) = await SeedSpaceAsync();
        var page = await SeedPageAsync(space, "alpha", "Alpha Handbook", labelName: "howto");

        using var response = await RunAsync(CreateUserClient(), $"space = {space.Key}");
        var connection = Connection(response);

        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        Assert.False(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.Empty(connection.GetProperty("errors").EnumerateArray());

        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("cursor").GetString()));
        Assert.Equal(page.Id.ToString(), edge.GetProperty("node").GetProperty("page").GetProperty("id").GetString());
        Assert.Equal("Alpha Handbook", edge.GetProperty("node").GetProperty("page").GetProperty("title").GetString());

        // design.md §21.13: the result list is a compilation and carries an aggregate label.
        Assert.Equal("UK OFFICIAL", connection.GetProperty("aggregateMarking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task BooleanOperators_And_Or_Not_And_Parentheses_Compose()
    {
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "a", "Alpha", labelName: "howto");
        await SeedPageAsync(space, "b", "Beta", labelName: "howto");
        await SeedPageAsync(space, "c", "Gamma", labelName: "reference");
        await SeedPageAsync(space, "d", "Delta");

        var client = CreateUserClient();

        async Task<string[]> QueryAsync(string filter)
        {
            using var response = await RunAsync(client, $"space = {space.Key} AND ({filter})");
            return Titles(Connection(response)).Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(["Alpha", "Beta"], await QueryAsync("label = howto"));
        Assert.Equal(["Alpha", "Beta", "Gamma"], await QueryAsync("label = howto OR label = reference"));
        Assert.Equal(["Alpha"], await QueryAsync("""label = howto AND title = \"Alpha\" """));
        Assert.Equal(["Delta", "Gamma"], await QueryAsync("NOT label = howto"));
        Assert.Equal(["Delta", "Gamma"], await QueryAsync("label != howto"));
        Assert.Equal(["Delta"], await QueryAsync("label IS EMPTY"));
        Assert.Equal(["Alpha", "Beta", "Gamma"], await QueryAsync("label IS NOT EMPTY"));
        Assert.Equal(["Alpha", "Beta", "Gamma"], await QueryAsync("label IN (howto, reference)"));
        Assert.Equal(["Delta"], await QueryAsync("label NOT IN (howto, reference)"));
        Assert.Equal(["Alpha"], await QueryAsync("""title ~ \"lph\" """));
        Assert.Equal(["Gamma"], await QueryAsync("""title !~ \"lph\" AND label IS NOT EMPTY AND label != howto"""));
    }

    [Fact]
    public async Task TitleContains_EscapesLikeWildcards()
    {
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "pct", "Margin 50% nominal");
        await SeedPageAsync(space, "other", "Margin 5099 nominal");

        using var response = await RunAsync(CreateUserClient(), $"""space = {space.Key} AND title ~ \"50%\" """);

        // Without escaping, "50%" would be a prefix search and would match both.
        Assert.Equal(["Margin 50% nominal"], Titles(Connection(response)));
    }

    [Fact]
    public async Task DatePredicates_UseDayPrecisionForBareDates_AndInstantsForNow()
    {
        var (space, _) = await SeedSpaceAsync();
        var old = new DateTime(2020, 1, 15, 9, 30, 0, DateTimeKind.Utc);
        await SeedPageAsync(space, "old", "Old", createdAtUtc: old, updatedAtUtc: old);
        await SeedPageAsync(space, "new", "New", createdAtUtc: DateTime.UtcNow, updatedAtUtc: DateTime.UtcNow);

        var client = CreateUserClient();

        using var before = await RunAsync(client, $"""space = {space.Key} AND created < \"2021-01-01\" """);
        Assert.Equal(["Old"], Titles(Connection(before)));

        // Day precision: `= "2020-01-15"` must match a page created at 09:30 that day, not
        // only one created at exactly midnight.
        using var sameDay = await RunAsync(client, $"""space = {space.Key} AND created = \"2020-01-15\" """);
        Assert.Equal(["Old"], Titles(Connection(sameDay)));

        using var rolling = await RunAsync(client, $"""space = {space.Key} AND updated > now(\"-1d\")""");
        Assert.Equal(["New"], Titles(Connection(rolling)));

        // Two now()s in one query see the same instant, so this window is never empty.
        using var window = await RunAsync(
            client, $"""space = {space.Key} AND updated > now(\"-1d\") AND updated <= now(\"+1h\")""");
        Assert.Equal(["New"], Titles(Connection(window)));
    }

    [Fact]
    public async Task Creator_MatchesTheFirstRevisionsAuthor_AndCurrentUserResolvesTheCaller()
    {
        var (space, seeder) = await SeedSpaceAsync();
        var callerSubject = $"rqlauthor-{Guid.NewGuid():N}";

        User caller;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            caller = new User
            {
                Subject = callerSubject,
                DisplayName = "Author",
                CreatedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(caller);
            await db.SaveChangesAsync();
        }

        await SeedPageAsync(space, "mine", "Mine", authorUserId: caller.Id);
        await SeedPageAsync(space, "theirs", "Theirs", authorUserId: seeder.Id);

        var client = CreateUserClient(sub: callerSubject);

        using var byFunction = await RunAsync(client, $"space = {space.Key} AND creator = currentUser()");
        Assert.Equal(["Mine"], Titles(Connection(byFunction)));

        using var bySubject = await RunAsync(client, $"""space = {space.Key} AND creator = \"{seeder.Subject}\" """);
        Assert.Equal(["Theirs"], Titles(Connection(bySubject)));

        using var negated = await RunAsync(client, $"space = {space.Key} AND creator != currentUser()");
        Assert.Equal(["Theirs"], Titles(Connection(negated)));
    }

    [Fact]
    public async Task OrderBy_IsHonoured_AndDefaultsToUpdatedDescending()
    {
        var (space, _) = await SeedSpaceAsync();
        var baseline = DateTime.UtcNow.AddHours(-5);
        await SeedPageAsync(space, "c", "Charlie", updatedAtUtc: baseline.AddMinutes(1));
        await SeedPageAsync(space, "a", "Alpha", updatedAtUtc: baseline.AddMinutes(3));
        await SeedPageAsync(space, "b", "Bravo", updatedAtUtc: baseline.AddMinutes(2));

        var client = CreateUserClient();

        using var byDefault = await RunAsync(client, $"space = {space.Key}");
        Assert.Equal(["Alpha", "Bravo", "Charlie"], Titles(Connection(byDefault)));

        using var byTitleDesc = await RunAsync(client, $"space = {space.Key} ORDER BY title DESC");
        Assert.Equal(["Charlie", "Bravo", "Alpha"], Titles(Connection(byTitleDesc)));

        using var byUpdatedAsc = await RunAsync(client, $"space = {space.Key} ORDER BY updated ASC");
        Assert.Equal(["Charlie", "Bravo", "Alpha"], Titles(Connection(byUpdatedAsc)));
    }

    [Fact]
    public async Task Pagination_UsesFirstAndCursors_WithoutOverlapOrGaps()
    {
        var (space, _) = await SeedSpaceAsync();
        var baseline = DateTime.UtcNow.AddHours(-3);
        for (var i = 0; i < 5; i++)
        {
            await SeedPageAsync(space, $"p{i}", $"Page {i}", updatedAtUtc: baseline.AddMinutes(i));
        }

        var client = CreateUserClient();

        using var first = await client.PostGraphQLAsync($$"""
            query { pageQuery(query: "space = {{space.Key}}", first: 2) { {{ConnectionSelection}} } }
            """);
        var firstPage = Connection(first);
        Assert.Equal(5, firstPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("edges").GetArrayLength());
        Assert.True(firstPage.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        var cursor = firstPage.GetProperty("pageInfo").GetProperty("endCursor").GetString();

        using var second = await client.PostGraphQLAsync($$"""
            query { pageQuery(query: "space = {{space.Key}}", first: 10, after: "{{cursor}}") { {{ConnectionSelection}} } }
            """);
        var secondPage = Connection(second);
        Assert.Equal(3, secondPage.GetProperty("edges").GetArrayLength());
        Assert.False(secondPage.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var all = Titles(firstPage).Concat(Titles(secondPage)).ToArray();
        Assert.Equal(5, all.Distinct().Count());
    }

    [Fact]
    public async Task TotalCount_SaturatesAtTheStatedCap()
    {
        var (space, _) = await SeedSpaceAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < 105; i++)
            {
                db.Pages.Add(new Page
                {
                    SpaceId = space.Id,
                    AncestorPath = "/",
                    Slug = $"cap-{i}",
                    Title = $"Capped {i:D3}",
                    CurrentContent = "capped",
                    CurrentRevisionNumber = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now.AddSeconds(-i),
                });
            }

            await db.SaveChangesAsync();
        }

        using var response = await RunAsync(CreateUserClient(), $"space = {space.Key}");

        // design.md §22: an honest cap, stated rather than hidden. Nothing in the response
        // says "there were more" - that would be a count over rows the filter removed.
        Assert.Equal(100, Connection(response).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task FirstArgument_IsClampedAndNeverWidensTheCap()
    {
        var (space, _) = await SeedSpaceAsync();
        for (var i = 0; i < 3; i++)
        {
            await SeedPageAsync(space, $"clamp-{i}", $"Clamp {i}");
        }

        var client = CreateUserClient();

        using var huge = await client.PostGraphQLAsync($$"""
            query { pageQuery(query: "space = {{space.Key}}", first: 100000) { {{ConnectionSelection}} } }
            """);
        Assert.Equal(3, Connection(huge).GetProperty("edges").GetArrayLength());

        using var zero = await client.PostGraphQLAsync($$"""
            query { pageQuery(query: "space = {{space.Key}}", first: 0) { {{ConnectionSelection}} } }
            """);
        Assert.Equal(1, Connection(zero).GetProperty("edges").GetArrayLength());
    }

    // ---------- absence (design.md §6.7 / §21.8) ----------

    [Fact]
    public async Task InvisibleSpace_NonexistentSpace_UnknownLabel_AndUnknownCreator_AreByteIdentical()
    {
        // A space granted only to a group the caller is not in, holding a page that WOULD
        // match. Proving it is really there matters: without the cleared-caller arm below,
        // this test would pass against a query engine that simply never worked.
        var (hidden, _) = await SeedSpaceAsync(RuleExpressionSerializer.Serialize(new GroupCondition("insiders")));
        await SeedPageAsync(hidden, "classified", "Classified Programme Notes", labelName: "blackproject");

        var outsider = CreateUserClient();

        using var invisibleSpace = await RunAsync(outsider, $"space = {hidden.Key}");
        using var nonexistentSpace = await RunAsync(outsider, "space = NOSUCHKEY");
        using var unknownLabel = await RunAsync(outsider, "label = blackproject");
        using var unknownCreator = await RunAsync(outsider, """creator = \"nobody-at-all\" """);

        var baseline = invisibleSpace.RootElement.GetRawText();
        Assert.Equal(baseline, nonexistentSpace.RootElement.GetRawText());
        Assert.Equal(baseline, unknownLabel.RootElement.GetRawText());
        Assert.Equal(baseline, unknownCreator.RootElement.GetRawText());

        // ...and the shape is genuinely "nothing here": no error, no hint, no aggregate.
        var connection = Connection(invisibleSpace);
        Assert.Equal(0, connection.GetProperty("totalCount").GetInt32());
        Assert.Empty(connection.GetProperty("edges").EnumerateArray());
        Assert.Empty(connection.GetProperty("errors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("aggregateMarking").ValueKind);
        Assert.DoesNotContain("Classified Programme Notes", baseline, StringComparison.Ordinal);

        // The page is really there, and a caller who holds the grant sees it - so the
        // emptiness above was the access rules, not a broken query.
        var insider = CreateUserClient(groups: ["insiders"]);
        using var insiderResponse = await RunAsync(insider, $"space = {hidden.Key}");
        Assert.Equal(["Classified Programme Notes"], Titles(Connection(insiderResponse)));
    }

    [Fact]
    public async Task RestrictedPage_IsAbsentEntirely_AndNotImpliedByTheCount()
    {
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "open", "Open Torque Notes", labelName: "shared");
        await SeedPageAsync(space, "locked", "Restricted Torque Overrides", labelName: "shared",
            restrictToGroup: "torque-cleared");

        using var response = await RunAsync(CreateUserClient(), $"space = {space.Key} AND label = shared");
        var connection = Connection(response);

        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        Assert.Equal(["Open Torque Notes"], Titles(connection));
        Assert.DoesNotContain("Restricted Torque Overrides", response.RootElement.GetRawText(), StringComparison.Ordinal);

        var cleared = CreateUserClient(groups: ["torque-cleared"]);
        using var clearedResponse = await RunAsync(cleared, $"space = {space.Key} AND label = shared");
        Assert.Equal(2, Connection(clearedResponse).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task OverClassifiedPages_AreExcluded_AndTheAggregateNeverRisesAboveWhatWasShown()
    {
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "official", "Official Ops Notes");
        await SeedPageAsync(space, "secret", "Secret Ops Notes", level: ClassificationLevel.Secret);

        // design.md §21.3: no clearance claim reads as OFFICIAL, so SECRET is out of reach.
        using var uncleared = await RunAsync(CreateUserClient(), $"space = {space.Key}");
        var unclearedConnection = Connection(uncleared);
        Assert.Equal(["Official Ops Notes"], Titles(unclearedConnection));
        Assert.DoesNotContain("Secret Ops Notes", uncleared.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("UK OFFICIAL", unclearedConnection.GetProperty("aggregateMarking").GetProperty("label").GetString());

        using var cleared = await RunAsync(CreateUserClient(clearance: "SECRET"), $"space = {space.Key}");
        var clearedConnection = Connection(cleared);
        Assert.Equal(2, clearedConnection.GetProperty("totalCount").GetInt32());
        Assert.Equal("UK SECRET", clearedConnection.GetProperty("aggregateMarking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task CrossSpaceQueries_AreGatedPerSpace()
    {
        var (open, _) = await SeedSpaceAsync();
        var (closed, _) = await SeedSpaceAsync(RuleExpressionSerializer.Serialize(new GroupCondition("closed-club")));
        var marker = $"marker{Guid.NewGuid():N}"[..14];
        await SeedPageAsync(open, "open", "Open Page", labelName: marker);
        await SeedPageAsync(closed, "closed", "Closed Page", labelName: marker);

        using var outsider = await RunAsync(CreateUserClient(), $"label = {marker}");
        Assert.Equal(["Open Page"], Titles(Connection(outsider)));

        using var member = await RunAsync(CreateUserClient(groups: ["closed-club"]), $"label = {marker}");
        Assert.Equal(["Closed Page", "Open Page"], Titles(Connection(member)).Order(StringComparer.Ordinal).ToArray());

        // Naming the closed space explicitly changes nothing for the outsider.
        using var byKey = await RunAsync(CreateUserClient(), $"space = {closed.Key}");
        Assert.Equal(0, Connection(byKey).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AnonymousCallers_GetAnEmptyConnection()
    {
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "anon", "Anonymous Bait");

        var client = factory.CreateClient();
        client.ClearTestUser();

        using var response = await RunAsync(client, $"space = {space.Key}");
        Assert.Equal(0, Connection(response).GetProperty("totalCount").GetInt32());
    }

    // ---------- errors ----------

    [Fact]
    public async Task InvalidQueries_ReturnPositionedErrorsRatherThanTopLevelFailures()
    {
        var client = CreateUserClient();

        using var forbidden = await RunAsync(client, """marking = \"SECRET\" """);
        var forbiddenErrors = Connection(forbidden).GetProperty("errors");
        Assert.Equal(0, Connection(forbidden).GetProperty("totalCount").GetInt32());
        var first = forbiddenErrors.EnumerateArray().Single();
        Assert.Equal("NOT_QUERYABLE_FIELD", first.GetProperty("code").GetString());
        Assert.Contains("not queryable", first.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Equal(0, first.GetProperty("offset").GetInt32());
        Assert.Equal(7, first.GetProperty("length").GetInt32());

        using var unsupported = await RunAsync(client, """text ~ \"x\" """);
        Assert.Equal("UNSUPPORTED_FIELD",
            Connection(unsupported).GetProperty("errors").EnumerateArray().Single().GetProperty("code").GetString());

        using var unknown = await RunAsync(client, """content ~ \"x\" """);
        Assert.Equal("UNKNOWN_FIELD",
            Connection(unknown).GetProperty("errors").EnumerateArray().Single().GetProperty("code").GetString());

        // No GraphQL top-level error for any of them: an unparseable query is authored
        // content, not a server fault.
        Assert.False(forbidden.RootElement.TryGetProperty("errors", out _));
    }

    // ---------- audit (design.md §7) ----------

    [Fact]
    public async Task PageQuery_WritesOneAuditRowPerQuery_WithTheQueryTextInDetails()
    {
        var (space, _) = await SeedSpaceAsync();
        await SeedPageAsync(space, "audited", "Audited Page");

        var marker = $"aud{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var client = CreateUserClient();
        using var _ = await RunAsync(client, $"space = {space.Key} AND title ~ {marker}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var row = Assert.Single(db.AuditEvents.Where(e => e.Action == "page.query" && e.DetailsJson!.Contains(marker)));

        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Equal(AuditChannel.GraphQl, row.Channel);
        Assert.NotNull(row.UserId);

        using var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Contains(marker, details.RootElement.GetProperty("query").GetString()!, StringComparison.Ordinal);
        Assert.True(details.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(0, details.RootElement.GetProperty("resultCount").GetInt32());
    }

    [Fact]
    public async Task TwoQueriesInOneDocument_EachGetTheirOwnAuditRow()
    {
        // A page full of list widgets issues several queries in one request; dedup keys on
        // the query text so the second is not swallowed as a duplicate of the first.
        var (space, _) = await SeedSpaceAsync();
        var marker = $"dup{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        using var _ = await CreateUserClient().PostGraphQLAsync($$"""
            query {
              one: pageQuery(query: "space = {{space.Key}} AND title ~ {{marker}}a") { totalCount }
              two: pageQuery(query: "space = {{space.Key}} AND title ~ {{marker}}b") { totalCount }
            }
            """);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        Assert.Equal(
            2, db.AuditEvents.Count(e => e.Action == "page.query" && e.DetailsJson!.Contains(marker)));
    }

    [Fact]
    public async Task AnInvalidQueryIsStillAudited()
    {
        var marker = $"bad{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        using var _ = await RunAsync(CreateUserClient(), $"clearance = {marker}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var row = Assert.Single(db.AuditEvents.Where(e => e.Action == "page.query" && e.DetailsJson!.Contains(marker)));

        using var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.False(details.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(AuditOutcome.Success, row.Outcome);
    }
}
