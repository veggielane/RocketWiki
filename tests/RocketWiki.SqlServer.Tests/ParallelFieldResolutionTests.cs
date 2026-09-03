using System.Text.Json;
using RocketWiki.Api.Tests.Integration;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// The SPA's page read, against a real engine, with every parallel-resolved field
/// selected at once.
///
/// This tier is where it belongs, and the reason is the whole point of the test.
/// Hot Chocolate resolves sibling fields in parallel, so several DataLoaders dispatch
/// their batches simultaneously; when they shared the request-scoped DbContext that is
/// "a second operation was started on this context instance before a previous operation
/// completed", surfaced to the client as an "Unexpected Execution Error" on each field.
/// Every page view in the SPA failed with "Couldn't load this page".
///
/// The SQLite tier could not catch it. In-process SQLite answers a batch fast enough
/// that the dispatches effectively serialize, so the window never opens; a real server
/// on a socket has enough latency that it always does. Selecting the fields one at a
/// time also passes — it is specifically the combination, which is why this asserts on
/// the SPA's actual field set (web/src/graphql/operations/page.graphql) rather than a
/// convenient subset.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ParallelFieldResolutionTests
{
    private readonly SqlServerContainerFixture _fixture;

    public ParallelFieldResolutionTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SqlServerFact]
    public async Task PageRead_SelectingEveryBatchedFieldAtOnce_ResolvesWithoutConcurrencyErrors()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        await using var factory = new SqlServerApiFactory(connectionString);
        var client = factory.CreateClient();

        // An instance admin creates the space (design.md §6.5) and grants the
        // engineering group SpaceAdmin plus access - a role confers no visibility
        // (§6.4), so the author needs both to write.
        client.SetTestUser(sub: "admin-user", name: "Admin", roles: ["admin"]);
        var spaceResult = await client.PostGraphQLAsync("""
            mutation {
              createSpace(
                input: { key: "PAR", name: "Parallel" }
                initialGrants: [
                  { kind: ROLE_GRANT, role: SPACE_ADMIN, expressionJson: "{\"group\":\"engineering\"}" }
                  { kind: ACCESS_GRANT, expressionJson: "{\"group\":\"engineering\"}" }
                ]
              ) { space { id } error { kind message } }
            }
            """);
        var space = spaceResult.RootElement.GetProperty("data").GetProperty("createSpace");
        Assert.Equal(JsonValueKind.Null, space.GetProperty("error").ValueKind);
        var spaceId = space.GetProperty("space").GetProperty("id").GetString();

        client.SetTestUser(sub: "author-user", name: "Author", groups: ["engineering"]);
        var pageResult = await client.PostGraphQLAsync($$"""
            mutation {
              createPage(input: {
                spaceId: "{{spaceId}}", slug: "parallel", title: "Parallel",
                content: "# Parallel\n\nbody"
              }) { page { id } error { kind message } }
            }
            """);
        var created = pageResult.RootElement.GetProperty("data").GetProperty("createPage");
        Assert.Equal(JsonValueKind.Null, created.GetProperty("error").ValueKind);
        var pageId = created.GetProperty("page").GetProperty("id").GetString();

        // The read the SPA actually issues. Every field below resolves through its own
        // DataLoader or service, and the executor dispatches them together.
        var read = await client.PostGraphQLAsync($$"""
            {
              page(id: "{{pageId}}") {
                id spaceId spaceKey title slug content currentRevisionNumber
                canEdit canComment canManageAccess viewerIsWatching labels
                labelDetails { id spaceId name }
                properties { keyId key value sortOrder }
                marking { level levelName eyesOnly ukPrefix label }
                parent { id title }
                children { id }
                comments { id body authorUserId author { id displayName hasAvatar } }
                attachments { id fileName uploadedBy { id displayName hasAvatar } }
              }
            }
            """);

        // The failure mode was per-field errors alongside a partially-null page, not a
        // transport failure — so assert on `errors` explicitly rather than trusting a
        // 200 and a non-null `data`.
        Assert.False(
            read.RootElement.TryGetProperty("errors", out var errors),
            $"page read returned GraphQL errors: {errors}");

        var page = read.RootElement.GetProperty("data").GetProperty("page");
        Assert.Equal("Parallel", page.GetProperty("title").GetString());
        Assert.Equal("PAR", page.GetProperty("spaceKey").GetString());
        // canEdit is one of the fields that used to come back null with an error
        // attached; the author holds SpaceAdmin through the engineering grant.
        Assert.True(page.GetProperty("canEdit").GetBoolean());
        Assert.False(page.GetProperty("viewerIsWatching").GetBoolean());
    }

    /// <summary>
    /// The same class of bug, one loader over. The fixture above has a page with NO
    /// children and runs no search, so <c>PageByIdDataLoader</c> — the loader that
    /// materializes a Page from an id — never receives more than one key in the only
    /// tier that can expose a concurrent-DbContext fault. It was also the last loader
    /// still resolving through a request-scoped <c>IPageReadService</c> rather than its
    /// own context.
    ///
    /// <para>Two keys is the whole point: with a shared context, a two-key batch issues
    /// overlapping operations and EF throws "a second operation was started on this
    /// context instance", surfacing as per-field execution errors and a partially-null
    /// page — which is precisely how every page view in the SPA broke on the first
    /// container run. SQLite cannot reproduce it (in-process, its async completes
    /// synchronously so the dispatches serialize); a real server on a socket always
    /// can.</para>
    ///
    /// <para>Both reachable shapes are covered in one document: <c>children</c> over two
    /// siblings, and a two-hit <c>search</c> whose edges resolve <c>page</c>.</para>
    /// </summary>
    [SqlServerFact]
    public async Task PageByIdDataLoader_WithSeveralKeysInOneBatch_ResolvesWithoutConcurrencyErrors()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        await using var factory = new SqlServerApiFactory(connectionString);
        var client = factory.CreateClient();

        client.SetTestUser(sub: "admin-user", name: "Admin", roles: ["admin"]);
        var spaceResult = await client.PostGraphQLAsync("""
            mutation {
              createSpace(
                input: { key: "KIDS", name: "Children" }
                initialGrants: [
                  { kind: ROLE_GRANT, role: SPACE_ADMIN, expressionJson: "{\"group\":\"engineering\"}" }
                  { kind: ACCESS_GRANT, expressionJson: "{\"group\":\"engineering\"}" }
                ]
              ) { space { id } error { kind message } }
            }
            """);
        var space = spaceResult.RootElement.GetProperty("data").GetProperty("createSpace");
        Assert.Equal(JsonValueKind.Null, space.GetProperty("error").ValueKind);
        var spaceId = space.GetProperty("space").GetProperty("id").GetString();

        client.SetTestUser(sub: "author-user", name: "Author", groups: ["engineering"]);

        async Task<string> CreateAsync(string slug, string title, string? parentId, string body)
        {
            var parent = parentId is null ? "null" : $"\"{parentId}\"";
            var created = await client.PostGraphQLAsync($$"""
                mutation {
                  createPage(input: {
                    spaceId: "{{spaceId}}", parentPageId: {{parent}},
                    slug: "{{slug}}", title: "{{title}}", content: "{{body}}"
                  }) { page { id } error { kind message } }
                }
                """);
            var payload = created.RootElement.GetProperty("data").GetProperty("createPage");
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
            return payload.GetProperty("page").GetProperty("id").GetString()!;
        }

        // A distinctive term so the search below returns exactly these two pages.
        const string term = "zzbatchterm";
        var parentId = await CreateAsync("parent", "Parent", null, "# Parent");
        var firstChild = await CreateAsync("child-one", "Child One", parentId, $"# One\\n\\n{term} alpha");
        var secondChild = await CreateAsync("child-two", "Child Two", parentId, $"# Two\\n\\n{term} beta");

        // SQL Server populates a full-text index asynchronously, so the search leg has to
        // wait for it or it contributes zero keys and stops testing anything. Polled the
        // same way FullTextSearchTests does, and deliberately BEFORE the combined document
        // below — the point of that document is two loaders dispatching in one tick, which
        // only holds if search actually returns hits.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            using var probe = await client.PostGraphQLAsync($$"""
                { search(query: "{{term}}") { totalCount } }
                """);
            if (probe.RootElement.GetProperty("data").GetProperty("search")
                .GetProperty("totalCount").GetInt32() >= 2)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        var read = await client.PostGraphQLAsync($$"""
            {
              page(id: "{{parentId}}") { id children { id title spaceKey canEdit } }
              search(query: "{{term}}") { totalCount edges { node { page { id title } } } }
            }
            """);

        // The failure mode was per-field execution errors beside partially-null data,
        // not a transport failure — so assert on `errors` explicitly.
        Assert.False(read.RootElement.TryGetProperty("errors", out var errors),
            $"batched page read returned GraphQL errors: {errors}");

        var children = read.RootElement.GetProperty("data").GetProperty("page")
            .GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.False(
            string.IsNullOrEmpty(child.GetProperty("title").GetString()),
            "a child resolved with a null title, which is what a failed batch looks like"));
        Assert.Equal(
            new[] { firstChild, secondChild }.OrderBy(id => id, StringComparer.Ordinal),
            children.Select(c => c.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal));

        var hits = read.RootElement.GetProperty("data").GetProperty("search")
            .GetProperty("edges").EnumerateArray().ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal(
            JsonValueKind.Object, hit.GetProperty("node").GetProperty("page").ValueKind));
    }
}
