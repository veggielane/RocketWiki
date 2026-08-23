using RocketWiki.Api.Tests.Integration;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// EmbeddingDimensionsStartupCheck on the environment it exists for: a real API host
/// (the SqlServerApiFactory of MigrateOnStartupApiTests) over a real SQL Server whose
/// PageEmbeddings.Embedding column is the migration's vector(1536). A configured
/// dimension count the column can never satisfy must refuse to boot — loudly, at
/// startup, naming the fix (a migration + full re-embed, data-model.md) — because the
/// alternative is a host whose every indexer write and every semantic query fails at
/// the engine. SQL Server-only by design: the SQLite tier's blob column fixes nothing,
/// so its tests legitimately run tiny vectors and this guard stays inert there.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class EmbeddingStartupGuardTests
{
    private readonly SqlServerContainerFixture _fixture;

    public EmbeddingStartupGuardTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SqlServerFact]
    public async Task Startup_WithDimensionsMismatchingTheVectorColumn_FailsLoudly()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        await using var factory = new SqlServerApiFactory(connectionString, new Dictionary<string, string?>
        {
            // The endpoint is never contacted: the guard rejects the configuration
            // before the background service can attempt an embedding call.
            ["ConnectionStrings:embeddings"] = "Endpoint=http://embeddings.invalid;Model=fake-model;Dimensions=8",
        });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        var messages = string.Join(" | ", Flatten(ex).Select(e => e.Message));
        Assert.Contains("vector(1536)", messages, StringComparison.Ordinal);
        Assert.Contains("migration", messages, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task Startup_WithMatchingDimensions_Boots_EvenIfTheEndpointIsUnreachable()
    {
        // Positive control for the guard's scope: dimensions agree with the column, so
        // the host must boot; an unreachable endpoint is §9.2 weather (background job
        // retries, search degrades to keyword-only), never a startup failure.
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        await using var factory = new SqlServerApiFactory(connectionString, new Dictionary<string, string?>
        {
            ["ConnectionStrings:embeddings"] = "Endpoint=http://embeddings.invalid;Model=fake-model;Dimensions=1536",
        });

        var client = factory.CreateClient();
        var response = await client.PostGraphQLAsync("{ me { isAuthenticated } }");
        Assert.False(response.RootElement
            .GetProperty("data").GetProperty("me").GetProperty("isAuthenticated").GetBoolean());
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
            {
                yield return inner;
            }
        }
        else if (exception.InnerException is { } single)
        {
            foreach (var inner in Flatten(single))
            {
                yield return inner;
            }
        }
    }
}
