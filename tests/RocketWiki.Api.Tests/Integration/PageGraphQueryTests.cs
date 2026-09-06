using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The document graph at the HTTP boundary (design.md §6.7 / §21.8): <c>pageGraph</c> and
/// the four <c>Page</c> link fields are OMITTING surfaces. A page the caller cannot view
/// is absent — no node, no edge touching it, no unit in any count — whichever gate hides
/// it (a restriction, a space they hold no access grant in, a selector, a caveat, the
/// trash, an archived space). A second, privileged caller proves the same fixture does
/// show those pages when the gate passes, so a filter that silently stopped filtering
/// could not pass by accident.
///
/// <code>
/// OPEN (access: everyone; editor: everyone; access: apple-readers {FRUIT/APPLE})
///   A  Alpha                       links B, R, N, H, T, S, Z, dangling  (ordinals 0..7)
///   B  Bravo                       links A
///   Q  Quebec                      links A            (A does not link back: the
///                                                      counts are asymmetric on purpose)
///   R  Restricted (view: group engineering)          links A
///   N  November   (NZ eyes only)                     links A
///   H  Hotel      (FRUIT/APPLE)                      links A
///   T  Tango      (trashed)                          links A
/// SEC (access: secret-club)
///   S  Sierra                                        links A
/// ARCH (archived)
///   Z  Zulu                                          links A
/// </code>
/// Plain: no groups, UK. Privileged: engineering + secret-club + apple-readers, NZ.
/// </summary>
public sealed class PageGraphQueryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string TitleA = "Alpha ZZATITLEZZ";
    private const string TitleB = "Bravo ZZBTITLEZZ";
    private const string TitleQ = "Quebec ZZQTITLEZZ";
    private const string TitleR = "Restricted ZZRTITLEZZ";
    private const string TitleN = "November ZZNTITLEZZ";
    private const string TitleH = "Hotel ZZHTITLEZZ";
    private const string TitleT = "Tango ZZTTITLEZZ";
    private const string TitleS = "Sierra ZZSTITLEZZ";
    private const string TitleZ = "Zulu ZZZTITLEZZ";

    private sealed record Fixture(
        Guid OpenSpaceId, string OpenKey, Guid SecretSpaceId, string SecretKey, string ArchivedKey,
        Guid A, Guid B, Guid Q, Guid R, Guid N, Guid H, Guid T, Guid S, Guid Z, Guid Dangling, Guid RestrictionRuleId)
    {
        public IEnumerable<Guid> HiddenFromPlain => [R, N, H, T, S, Z, Dangling];

        public IEnumerable<string> HiddenTitles => [TitleR, TitleN, TitleH, TitleT, TitleS, TitleZ];
    }

    private const string GraphSelection =
        "nodes { id title spaceKey slug icon marking { label } } edges { sourcePageId targetPageId ordinal }";

    private const string LinksSelection =
        "outboundLinks { id title } outboundLinkCount inboundLinks { id title } inboundLinkCount";

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var now = DateTime.UtcNow;
        var creator = new User
        {
            Subject = $"graph-seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = now, LastSeenAtUtc = now,
        };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        Space NewSpace(string prefix, string name) => new()
        {
            Key = $"{prefix}{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = name,
            OriginInstanceId = "standalone", CreatedAtUtc = now, CreatedByUserId = creator.Id, OwnerUserId = creator.Id,
        };

        var open = NewSpace("GRO", "Graph Open");
        var secret = NewSpace("GRS", "Graph Secret");
        var archived = NewSpace("GRA", "Graph Archived");
        archived.IsDeleted = true;
        archived.DeletedAtUtc = now;
        db.Spaces.AddRange(open, secret, archived);

        AccessRule Grant(Guid spaceId, RuleNode expression, AccessRuleKind kind = AccessRuleKind.AccessGrant, SpaceRole? role = null) => new()
        {
            Kind = kind, SpaceId = spaceId, Role = role,
            ExpressionJson = RuleExpressionSerializer.Serialize(expression),
            CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
        };

        var appleGrant = Grant(open.Id, new GroupCondition("apple-readers"));
        appleGrant.Selectors.Add(new AccessRuleSelector { Category = "FRUIT", Value = "APPLE" });
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(
            Grant(open.Id, new EveryoneCondition(), AccessRuleKind.RoleGrant, SpaceRole.Editor)));
        db.AccessRules.Add(appleGrant);
        db.AccessRules.Add(Grant(secret.Id, new GroupCondition("secret-club")));
        db.AccessRules.Add(Grant(archived.Id, new EveryoneCondition()));

        Page NewPage(Space space, string slug, string title) => new()
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = slug, Title = title, CreatedAtUtc = now, UpdatedAtUtc = now,
        };

        var a = NewPage(open, "alpha", TitleA);
        var b = NewPage(open, "bravo", TitleB);
        var q = NewPage(open, "quebec", TitleQ);
        var r = NewPage(open, "restricted", TitleR);
        var n = NewPage(open, "november", TitleN);
        var h = NewPage(open, "hotel", TitleH);
        var t = NewPage(open, "tango", TitleT);
        t.IsDeleted = true;
        t.DeletedAtUtc = now;
        var s = NewPage(secret, "sierra", TitleS);
        var z = NewPage(archived, "zulu", TitleZ);
        var dangling = Guid.NewGuid();

        static string Link(Guid id) => $"page://{id}";
        a.CurrentContent = string.Join(" ", new[] { b.Id, r.Id, n.Id, h.Id, t.Id, s.Id, z.Id, dangling }.Select(Link));
        foreach (var page in new[] { b, q, r, n, h, t, s, z })
        {
            page.CurrentContent = Link(a.Id);
        }

        var pages = new[] { a, b, q, r, n, h, t, s, z };
        db.Pages.AddRange(pages);

        // One SaveChanges for the whole graph, with an explicit marking per page: an
        // intermediate save would let the every-page-is-marked backstop materialize
        // markings that collide with the explicit ones.
        foreach (var page in pages)
        {
            var marking = new PageMarking { PageId = page.Id, Level = ClassificationLevel.Official, SetAtUtc = now };
            if (page == n)
            {
                marking.Countries.Add(new PageMarkingCountry { PageId = n.Id, CountryValue = "NZ" });
            }

            if (page == h)
            {
                marking.Selectors.Add(new PageMarkingSelector { PageId = h.Id, Category = "FRUIT", Value = "APPLE" });
            }

            db.PageMarkings.Add(marking);
        }

        var restriction = new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction, PageId = r.Id, Action = PageAction.View,
            ExpressionJson = RuleExpressionSerializer.Serialize(new GroupCondition("engineering")),
            CreatedAtUtc = now, CreatedByUserId = creator.Id, UpdatedAtUtc = now, UpdatedByUserId = creator.Id,
        };
        db.AccessRules.Add(restriction);

        // The index rows exactly as the product's write paths would have written them
        // (PageLink.FromContent is THE definition of a page's row set; the four write
        // paths are pinned in RocketWiki.Data.Tests). One test below goes through the
        // real createPage mutation as well.
        foreach (var page in pages)
        {
            db.PageLinks.AddRange(PageLink.FromContent(page.Id, page.CurrentContent));
        }

        await db.SaveChangesAsync();

        return new Fixture(
            open.Id, open.Key, secret.Id, secret.Key, archived.Key,
            a.Id, b.Id, q.Id, r.Id, n.Id, h.Id, t.Id, s.Id, z.Id, dangling, restriction.Id);
    }

    private (HttpClient Client, string Sub) PlainClient()
    {
        var sub = $"graph-plain-{Guid.NewGuid():N}";
        var client = factory.CreateClient();
        client.SetTestUser(sub, nationality: ["UK"]);
        return (client, sub);
    }

    private (HttpClient Client, string Sub) PrivilegedClient()
    {
        var sub = $"graph-priv-{Guid.NewGuid():N}";
        var client = factory.CreateClient();
        client.SetTestUser(sub, groups: ["engineering", "secret-club", "apple-readers"], nationality: ["NZ"]);
        return (client, sub);
    }

    private static string GraphQuery(string? spaceKey) =>
        spaceKey is null
            ? $$"""{ pageGraph { {{GraphSelection}} } }"""
            : $$"""{ pageGraph(spaceKey: "{{spaceKey}}") { {{GraphSelection}} } }""";

    private static (List<Guid> NodeIds, List<(Guid Source, Guid Target, int Ordinal)> Edges) Parse(JsonElement graph)
    {
        var nodes = graph.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();
        var edges = graph.GetProperty("edges").EnumerateArray()
            .Select(e => (e.GetProperty("sourcePageId").GetGuid(), e.GetProperty("targetPageId").GetGuid(), e.GetProperty("ordinal").GetInt32()))
            .ToList();
        return (nodes, edges);
    }

    private static JsonElement Data(JsonDocument doc, string field) => doc.RootElement.GetProperty("data").GetProperty(field);

    private static void AssertNoErrors(JsonDocument doc) =>
        Assert.False(doc.RootElement.TryGetProperty("errors", out var errors), $"query errored: {errors}");

    private async Task<List<AuditEvent>> GraphAuditRowsAsync(string sub)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Subject == sub);
        return user is null
            ? []
            : await db.AuditEvents.Where(e => e.UserId == user.Id && e.Action == "graph.view").OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<int> TotalAuditRowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>().AuditEvents.CountAsync();
    }

    // ================================================================== pageGraph

    [Fact]
    public async Task PageGraph_OmitsEveryPageTheCallerCannotView_AndEveryEdgeTouchingOne()
    {
        var f = await SeedAsync();
        var (plain, _) = PlainClient();

        using var result = await plain.PostGraphQLAsync(GraphQuery(null));
        AssertNoErrors(result);
        var body = result.RootElement.ToString();
        var (nodes, edges) = Parse(Data(result, "pageGraph"));

        // The graph is instance-wide, so other fixtures' pages may be in it; this
        // fixture's contribution is exactly A, B and Q, and the three edges among them.
        Assert.Contains(f.A, nodes);
        Assert.Contains(f.B, nodes);
        Assert.Contains(f.Q, nodes);
        Assert.Contains((f.A, f.B, 0), edges);
        Assert.Contains((f.B, f.A, 0), edges);
        Assert.Contains((f.Q, f.A, 0), edges);
        foreach (var hidden in f.HiddenFromPlain)
        {
            Assert.DoesNotContain(hidden, nodes);
            Assert.DoesNotContain(edges, e => e.Source == hidden || e.Target == hidden);
            Assert.DoesNotContain(JsonSerializer.Serialize(hidden.ToString()), body, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var title in f.HiddenTitles)
        {
            Assert.DoesNotContain(title, body, StringComparison.Ordinal);
        }

        // No placeholder vocabulary on an omitting surface (§21.8).
        Assert.DoesNotContain(AccessDenialView.ProtectedTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain("denial", body, StringComparison.Ordinal);

        // A visible node carries what the screen draws and navigates by.
        var nodeA = Data(result, "pageGraph").GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetGuid() == f.A);
        Assert.Equal(TitleA, nodeA.GetProperty("title").GetString());
        Assert.Equal(f.OpenKey, nodeA.GetProperty("spaceKey").GetString());
        Assert.Equal("alpha", nodeA.GetProperty("slug").GetString());
        Assert.Equal("UK OFFICIAL", nodeA.GetProperty("marking").GetProperty("label").GetString());
    }

    [Fact]
    public async Task PageGraph_ForACallerWhoPassesTheGates_ShowsTheHiddenPages_SoTheFilterIsThePrincipal()
    {
        var f = await SeedAsync();
        var (privileged, _) = PrivilegedClient();

        using var result = await privileged.PostGraphQLAsync(GraphQuery(null));
        AssertNoErrors(result);
        var (nodes, edges) = Parse(Data(result, "pageGraph"));

        foreach (var visible in new[] { f.A, f.B, f.Q, f.R, f.N, f.H, f.S })
        {
            Assert.Contains(visible, nodes);
        }

        // Every backlink to A, hidden-for-plain ones included, is an edge for this caller.
        foreach (var source in new[] { f.B, f.Q, f.R, f.N, f.H, f.S })
        {
            Assert.Contains((source, f.A, 0), edges);
        }

        // A's outbound edges keep the content's ordinals even where a hidden target
        // between them was dropped: T (4), Z (6) and the dangling id (7) are no edges,
        // and S stays at 5.
        Assert.Equal(
            [(f.A, f.B, 0), (f.A, f.R, 1), (f.A, f.N, 2), (f.A, f.H, 3), (f.A, f.S, 5)],
            edges.Where(e => e.Source == f.A).OrderBy(e => e.Ordinal));

        // The trash and an archived space hide a page from everyone.
        foreach (var never in new[] { f.T, f.Z, f.Dangling })
        {
            Assert.DoesNotContain(never, nodes);
            Assert.DoesNotContain(edges, e => e.Source == never || e.Target == never);
        }
    }

    [Fact]
    public async Task PageGraph_ScopedToASpace_IsTheInducedSubgraph_AndTheKeyIsCanonicalized()
    {
        var f = await SeedAsync();
        var (privileged, _) = PrivilegedClient();

        // Lower-cased key: scopes to OPEN, not to nothing.
        using var result = await privileged.PostGraphQLAsync(GraphQuery(f.OpenKey.ToLowerInvariant()));
        AssertNoErrors(result);
        var (nodes, edges) = Parse(Data(result, "pageGraph"));

        Assert.Equal(new[] { f.A, f.B, f.Q, f.H, f.N, f.R }.Order(), nodes.Order());
        // S is viewable by this caller and linked both ways with A, but lives outside the
        // scope: not a node here, so neither A->S nor S->A is an edge.
        Assert.DoesNotContain(f.S, nodes);
        Assert.DoesNotContain(edges, e => e.Source == f.S || e.Target == f.S);
        var expectedEdges = new[]
        {
            (f.A, f.B, 0), (f.A, f.R, 1), (f.A, f.N, 2), (f.A, f.H, 3),
            (f.B, f.A, 0), (f.Q, f.A, 0), (f.H, f.A, 0), (f.N, f.A, 0), (f.R, f.A, 0),
        };
        Assert.Equal(
            expectedEdges.OrderBy(e => e.Item1).ThenBy(e => e.Item2),
            edges.OrderBy(e => e.Source).ThenBy(e => e.Target));

        // Every node is in the scoped space.
        foreach (var node in Data(result, "pageGraph").GetProperty("nodes").EnumerateArray())
        {
            Assert.Equal(f.OpenKey, node.GetProperty("spaceKey").GetString());
        }
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("archived")]
    public async Task PageGraph_ASpaceTheCallerCannotEnter_IsByteIdenticalToAKeyNamingNothing(string which)
    {
        // §6.7: no such space, an archived space, and a space no access grant admits the
        // caller to all answer the same empty graph - at the transport, byte for byte.
        // Every key is 8 characters, interpolated into the request and never echoed.
        var f = await SeedAsync();
        var (plain, sub) = PlainClient();
        var key = which == "secret" ? f.SecretKey : f.ArchivedKey;
        var missingKey = $"NON{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        var scoped = await plain.PostAsJsonAsync("/graphql", new { query = GraphQuery(key) });
        var missing = await plain.PostAsJsonAsync("/graphql", new { query = GraphQuery(missingKey) });

        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        Assert.Equal(missing.StatusCode, scoped.StatusCode);
        var scopedBody = await scoped.Content.ReadAsStringAsync();
        Assert.Equal(await missing.Content.ReadAsStringAsync(), scopedBody);
        Assert.Contains("\"nodes\":[]", scopedBody, StringComparison.Ordinal);
        Assert.Contains("\"edges\":[]", scopedBody, StringComparison.Ordinal);

        // "You saw nothing" is a different fact from "nothing happened" (§7): the caller
        // was shown an empty graph, and the audit table says so - one graph.view Success
        // row for the requested scope with a zero node count, never silence. The live
        // (secret) space is the row's subject; an archived one is hidden by the Space
        // query filter before it can be, so its row carries the key and no subject id.
        var row = Assert.Single(await GraphAuditRowsAsync(sub), r => r.SpaceKey == key);
        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Equal(which == "secret" ? f.SecretSpaceId : null, row.SubjectId);
        var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
        Assert.Equal(key, details.GetProperty("scope").GetString());
        Assert.Equal(0, details.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, details.GetProperty("edgeCount").GetInt32());

        // And the privileged caller proves SEC does hold S: the emptiness above was the
        // gate, not an empty fixture.
        if (which == "secret")
        {
            var (privileged, _) = PrivilegedClient();
            using var seen = await privileged.PostGraphQLAsync(GraphQuery(key));
            Assert.Equal([f.S], Parse(Data(seen, "pageGraph")).NodeIds);
        }
    }

    [Fact]
    public async Task PageGraph_Anonymous_IsEmpty_AndAuditsNothing()
    {
        await SeedAsync();
        var client = factory.CreateClient(); // no SetTestUser
        var before = await TotalAuditRowsAsync();

        using var result = await client.PostGraphQLAsync(GraphQuery(null));
        AssertNoErrors(result);
        var (nodes, edges) = Parse(Data(result, "pageGraph"));
        Assert.Empty(nodes);
        Assert.Empty(edges);
        Assert.Equal(before, await TotalAuditRowsAsync());
    }

    [Fact]
    public async Task PageGraph_IsAuditedOnceAsGraphView_WithScopeAndCounts()
    {
        var f = await SeedAsync();
        var (plain, sub) = PlainClient();

        // Two scopes in one document: two rows (dedup is by scope), each Success, each
        // carrying what the caller was handed and nothing more.
        using var result = await plain.PostGraphQLAsync($$"""
            {
              whole: pageGraph { nodes { id } edges { ordinal } }
              scoped: pageGraph(spaceKey: "{{f.OpenKey.ToLowerInvariant()}}") { nodes { id } edges { ordinal } }
              nowhere: pageGraph(spaceKey: "NOPE{{f.OpenKey[..4]}}") { nodes { id } }
            }
            """);
        AssertNoErrors(result);
        var wholeNodeCount = Data(result, "whole").GetProperty("nodes").GetArrayLength();
        var wholeEdgeCount = Data(result, "whole").GetProperty("edges").GetArrayLength();

        var rows = await GraphAuditRowsAsync(sub);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("graph.view", row.Action);
            Assert.Equal(AuditOutcome.Success, row.Outcome);
            Assert.Equal(AuditChannel.GraphQl, row.Channel);
            Assert.NotNull(row.DetailsJson);
        });

        var whole = Assert.Single(rows, r => r.SpaceKey is null);
        Assert.Null(whole.SubjectType);
        Assert.Null(whole.SubjectId);
        var wholeDetails = JsonDocument.Parse(whole.DetailsJson!).RootElement;
        Assert.Equal("instance", wholeDetails.GetProperty("scope").GetString());
        Assert.Equal(wholeNodeCount, wholeDetails.GetProperty("nodeCount").GetInt32());
        Assert.Equal(wholeEdgeCount, wholeDetails.GetProperty("edgeCount").GetInt32());

        var scoped = Assert.Single(rows, r => r.SpaceKey == f.OpenKey);
        Assert.Equal(AuditSubjectType.Space, scoped.SubjectType);
        Assert.Equal(f.OpenSpaceId, scoped.SubjectId);
        var scopedDetails = JsonDocument.Parse(scoped.DetailsJson!).RootElement;
        Assert.Equal(f.OpenKey, scopedDetails.GetProperty("scope").GetString());
        Assert.Equal(3, scopedDetails.GetProperty("nodeCount").GetInt32());
        Assert.Equal(3, scopedDetails.GetProperty("edgeCount").GetInt32());

        // A key naming nothing: the requested (canonical) key is recorded, no subject id.
        var nowhere = Assert.Single(rows, r => r.SpaceKey == $"NOPE{f.OpenKey[..4]}");
        Assert.Null(nowhere.SubjectId);
        Assert.Equal(0, JsonDocument.Parse(nowhere.DetailsJson!).RootElement.GetProperty("nodeCount").GetInt32());

        // Never a title or a marking in the row (§15's rule applies to the audit
        // details too: counts and scope are bounded, page content is not).
        foreach (var row in rows)
        {
            Assert.DoesNotContain(TitleA, row.DetailsJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("OFFICIAL", row.DetailsJson!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PageGraph_APageCreatedThroughTheApi_AppearsWithItsEdge()
    {
        // End to end through the product's own write path: createPage indexes the links
        // in the same unit of work, and the next graph read shows the new node and edge.
        var f = await SeedAsync();
        var (privileged, _) = PrivilegedClient();

        using var created = await privileged.PostGraphQLAsync($$"""
            mutation { createPage(input: {
                spaceId: "{{f.OpenSpaceId}}", slug: "{{$"new-{Guid.NewGuid():N}"[..16]}}", title: "Newcomer",
                content: "see page://{{f.A}} and page://{{f.R}}"
              }) { page { id } error { kind message } } }
            """);
        AssertNoErrors(created);
        var payload = Data(created, "createPage");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        var newId = payload.GetProperty("page").GetProperty("id").GetGuid();

        using var result = await privileged.PostGraphQLAsync(GraphQuery(f.OpenKey));
        var (nodes, edges) = Parse(Data(result, "pageGraph"));
        Assert.Contains(newId, nodes);
        Assert.Contains((newId, f.A, 0), edges);
        Assert.Contains((newId, f.R, 1), edges);

        // And the plain caller, who cannot view R, sees the new page's edge to A only.
        var (plain, _) = PlainClient();
        using var plainResult = await plain.PostGraphQLAsync(GraphQuery(f.OpenKey));
        var plainEdges = Parse(Data(plainResult, "pageGraph")).Edges.Where(e => e.Source == newId).ToList();
        Assert.Equal([(newId, f.A, 0)], plainEdges);
    }

    // ================================================================== Page link fields

    [Fact]
    public async Task PageLinks_ListOnlyVisibleNeighbours_AndEachCountIsItsListsLength()
    {
        var f = await SeedAsync();

        // Plain: of A's eight links only B is viewable; of the eight backlinks only B's
        // and Q's. The two counts differ on purpose, so a count wired to the wrong list
        // (or to anything but its own list) cannot pass.
        var (plain, plainSub) = PlainClient();
        using var plainResult = await plain.PostGraphQLAsync($$"""{ page(id: "{{f.A}}") { id {{LinksSelection}} } }""");
        AssertNoErrors(plainResult);
        var plainPage = Data(plainResult, "page");
        var plainBody = plainResult.RootElement.ToString();

        Assert.Equal([f.B], Ids(plainPage, "outboundLinks"));
        Assert.Equal(1, plainPage.GetProperty("outboundLinkCount").GetInt32());
        Assert.Equal([f.B, f.Q], Ids(plainPage, "inboundLinks"));
        Assert.Equal(2, plainPage.GetProperty("inboundLinkCount").GetInt32());
        foreach (var hidden in f.HiddenFromPlain)
        {
            Assert.DoesNotContain(JsonSerializer.Serialize(hidden.ToString()), plainBody, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var title in f.HiddenTitles)
        {
            Assert.DoesNotContain(title, plainBody, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(AccessDenialView.ProtectedTitle, plainBody, StringComparison.Ordinal);

        // Privileged: outbound in the content's order (T, Z and the dangling id are not
        // live, so they are simply not there); inbound by title.
        var (privileged, _) = PrivilegedClient();
        using var privResult = await privileged.PostGraphQLAsync($$"""{ page(id: "{{f.A}}") { id {{LinksSelection}} } }""");
        AssertNoErrors(privResult);
        var privPage = Data(privResult, "page");

        Assert.Equal([f.B, f.R, f.N, f.H, f.S], Ids(privPage, "outboundLinks"));
        Assert.Equal(5, privPage.GetProperty("outboundLinkCount").GetInt32());
        Assert.Equal([f.B, f.H, f.N, f.Q, f.R, f.S], Ids(privPage, "inboundLinks"));
        Assert.Equal(6, privPage.GetProperty("inboundLinkCount").GetInt32());

        // The count is the list's length for every caller - the property the drill breaks.
        foreach (var page in new[] { plainPage, privPage })
        {
            Assert.Equal(page.GetProperty("outboundLinks").GetArrayLength(), page.GetProperty("outboundLinkCount").GetInt32());
            Assert.Equal(page.GetProperty("inboundLinks").GetArrayLength(), page.GetProperty("inboundLinkCount").GetInt32());
        }

        // No per-neighbour audit rows (§7): the read of A is the audited action, and a
        // hidden backlink is pruned, not refused.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var plainUser = await db.Users.SingleAsync(u => u.Subject == plainSub);
        var plainRows = await db.AuditEvents.Where(e => e.UserId == plainUser.Id).ToListAsync();
        var aRow = Assert.Single(plainRows, r => r.SubjectId == f.A);
        Assert.Equal("page.view", aRow.Action);
        Assert.Equal(AuditOutcome.Success, aRow.Outcome);
        Assert.DoesNotContain(plainRows, r => r.SubjectId is { } id && f.HiddenFromPlain.Contains(id));

        static List<Guid> Ids(JsonElement page, string field) =>
            page.GetProperty(field).EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task PageLinks_TheFourFieldsShareOneDecision_NotOneServiceCallEach()
    {
        // The loader is what makes "count is the list's length" also cheap: four fields
        // on one page cost one PageLinks read, observed on the SQL rather than assumed
        // from the wiring. The index read is the only query that filters on SourcePageId.
        var f = await SeedAsync();
        var (plain, _) = PlainClient();
        using (await plain.PostGraphQLAsync("{ me { localUserId } }"))
        {
        }

        using var counter = new EfSelectCommandCounter(factory, text => text.Contains("\"SourcePageId\" ="));
        using var result = await plain.PostGraphQLAsync($$"""{ page(id: "{{f.A}}") { {{LinksSelection}} } }""");
        AssertNoErrors(result);

        Assert.Single(counter.MatchedCommands);
    }

    [Fact]
    public async Task PageLinks_OnATrashedPage_AreEmpty_WithoutADenial()
    {
        // The trash listing resolves a trashed Page; its links are NotFound to the graph
        // (not live), which is an absence, not a refusal: empty lists, zero counts, no row.
        var f = await SeedAsync();
        var (privileged, sub) = PrivilegedClient();

        using var result = await privileged.PostGraphQLAsync($$"""
            { space(key: "{{f.OpenKey}}") { trashedPages { id {{LinksSelection}} } } }
            """);
        AssertNoErrors(result);
        var trashed = Assert.Single(
            Data(result, "space").GetProperty("trashedPages").EnumerateArray(),
            p => p.GetProperty("id").GetGuid() == f.T);
        Assert.Empty(trashed.GetProperty("outboundLinks").EnumerateArray());
        Assert.Empty(trashed.GetProperty("inboundLinks").EnumerateArray());
        Assert.Equal(0, trashed.GetProperty("outboundLinkCount").GetInt32());
        Assert.Equal(0, trashed.GetProperty("inboundLinkCount").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var user = await db.Users.SingleAsync(u => u.Subject == sub);
        Assert.Empty(await db.AuditEvents.Where(e => e.UserId == user.Id && e.SubjectId == f.T && e.Outcome == AuditOutcome.Denied).ToListAsync());
    }

    /// <summary>
    /// The race-only branch: the service re-decides the subject page and can answer
    /// Denied if a rule landed between the root read and the field. No HTTP request can
    /// reach it on purpose (a page that fails the gate never resolves as a Page), so the
    /// resolvers' shared collapse is driven directly, through the real loader against the
    /// real database, with a principal the restriction refuses: the denial is audited as a
    /// <c>page.view</c> refusal of that page with the rule engine's reason, and the result
    /// is null (which every field renders as its empty shape).
    /// </summary>
    [Fact]
    public async Task PageLinks_WhenTheServiceDeniesTheSubject_TheDenialIsAudited_AndTheListIsEmpty()
    {
        var f = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<RocketWikiDbContext>>();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var restricted = await db.Pages.SingleAsync(p => p.Id == f.R);

        var refused = new StubPrincipalAccessor(Principal.Create("refused-sub", []));
        var sink = new CapturingAuditSink();
        var loader = new PageLinkNeighboursByPageIdDataLoader(dbOptions, refused, new AutoBatchScheduler());

        var neighbours = await PageFieldResolvers.LoadPageLinksAsync(restricted, refused, sink, loader, CancellationToken.None);

        Assert.Null(neighbours);
        var record = Assert.Single(sink.Records);
        Assert.Equal("page.view", record.Action);
        Assert.Equal(AuditOutcome.Denied, record.Outcome);
        Assert.Equal(AuditSubjectType.Page, record.SubjectType);
        Assert.Equal(f.R, record.SubjectId);
        Assert.Equal(
            $"restriction:{f.R}:{f.RestrictionRuleId}",
            JsonDocument.Parse(record.DetailsJson!).RootElement.GetProperty("reason").GetString());

        // Positive control on the same wiring: a principal the rule admits gets the lists
        // and no row.
        var admitted = new StubPrincipalAccessor(Principal.Create("admitted-sub", ["engineering"]));
        var quietSink = new CapturingAuditSink();
        var admittedLoader = new PageLinkNeighboursByPageIdDataLoader(dbOptions, admitted, new AutoBatchScheduler());
        var seen = await PageFieldResolvers.LoadPageLinksAsync(restricted, admitted, quietSink, admittedLoader, CancellationToken.None);
        Assert.NotNull(seen);
        Assert.Equal([f.A], seen.Outbound.Select(n => n.Id));
        Assert.Empty(quietSink.Records);
    }

    // ================================================================== schema pins

    [Fact]
    public async Task GraphTypes_ExposeExactlyTheAllowlistedFields_AndNoDenialShape()
    {
        // §21.8's allowlist rule, applied to the omitting side: a node can carry only what
        // a viewable page's screen needs, and nothing that could describe a page the
        // caller cannot view - no denial, no placeholder title, no reasons, no counts of
        // hidden things. A field added here fails until it is argued for in design.md.
        await SeedAsync();
        var (plain, _) = PlainClient();

        using var introspection = await plain.PostGraphQLAsync("""
            {
              graph: __type(name: "PageGraph") { fields { name } }
              node: __type(name: "PageGraphNode") { fields { name } }
              edge: __type(name: "PageGraphEdge") { fields { name } }
              page: __type(name: "Page") { fields { name } }
            }
            """);
        static HashSet<string> Fields(JsonElement type) =>
            type.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToHashSet();

        Assert.Equal(new HashSet<string> { "nodes", "edges" }, Fields(Data(introspection, "graph")));
        Assert.Equal(
            new HashSet<string> { "id", "title", "spaceKey", "slug", "icon", "marking" },
            Fields(Data(introspection, "node")));
        Assert.Equal(
            new HashSet<string> { "sourcePageId", "targetPageId", "ordinal" },
            Fields(Data(introspection, "edge")));

        var pageFields = Fields(Data(introspection, "page"));
        foreach (var linkField in new[] { "outboundLinks", "inboundLinks", "outboundLinkCount", "inboundLinkCount" })
        {
            Assert.Contains(linkField, pageFields);
        }
    }

    private sealed class StubPrincipalAccessor(Principal? principal) : ICurrentPrincipalAccessor
    {
        public Principal? Current { get; } = principal;
    }

    private sealed class CapturingAuditSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(AuditRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}
