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
        // engineering group SpaceAdmin, which is what makes the author able to write.
        client.SetTestUser(sub: "admin-user", name: "Admin", roles: ["admin"]);
        var spaceResult = await client.PostGraphQLAsync("""
            mutation {
              createSpace(
                input: { key: "PAR", name: "Parallel" }
                initialGrant: { role: SPACE_ADMIN, expressionJson: "{\"group\":\"engineering\"}" }
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
                marking { level levelName eyesOnly prefix label }
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
}
