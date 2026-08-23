using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// The one SQL Server container for the whole test run (collection fixture: container
/// startup is the expensive part - ~1.5 GB image plus engine boot - so every test
/// class shares this instance and isolates itself with its own database instead; see
/// <see cref="SqlServerTestBase"/>).
///
/// The image is the FTS-enabled derivative defined in mssql-fts/Dockerfile - the
/// stock mssql/server image cannot even apply the InitialCreate migration (its
/// FULLTEXT DDL needs Full-Text Search installed). Resolution order:
///  1. ROCKETWIKI_MSSQL_FTS_IMAGE, when set, names a pre-built image - CI builds the
///     Dockerfile as its own step (debuggable, layer-cached) and passes the tag here.
///     This is an optimization hook, NOT a gate: forgetting it merely means the
///     fixture builds the image itself, slower but identical - never a silent skip.
///  2. Otherwise the fixture builds mssql-fts/Dockerfile via Testcontainers'
///     ImageFromDockerfile under a fixed tag, so a developer with Docker just runs
///     `dotnet test` (first build is minutes; the daemon's layer cache makes every
///     later run near-instant).
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    /// <summary>Fixed local tag so the daemon's layer cache is reused across runs.</summary>
    private const string LocalImageName = "rocketwiki-mssql-fts:2025";

    private MsSqlContainer? _container;

    public MsSqlContainer Container => _container
        ?? throw new InvalidOperationException(
            "The SQL Server container was never started. Tests reaching this fixture must be " +
            "gated by [SqlServerFact], which skips when Docker is unavailable.");

    public async Task InitializeAsync()
    {
        // xUnit constructs collection fixtures even when every test in the collection
        // is (statically) skipped - so with no Docker this must be a clean no-op, not
        // a throw, or the skip path would fail the run anyway.
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        var imageName = Environment.GetEnvironmentVariable("ROCKETWIKI_MSSQL_FTS_IMAGE");
        if (string.IsNullOrWhiteSpace(imageName))
        {
            var image = new ImageFromDockerfileBuilder()
                .WithName(LocalImageName)
                .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "mssql-fts"))
                .WithDockerfile("Dockerfile")
                .WithDeleteIfExists(false) // reuse the cached image instead of rebuilding from zero
                .WithCleanUp(false)        // keep it for the next run - rebuilding is the expensive part
                .Build();
            await image.CreateAsync();
            imageName = image.FullName;
        }

        _container = new MsSqlBuilder(imageName).Build();
        await _container.StartAsync();
    }

    /// <summary>
    /// Connection string for one isolated database on the shared container. The
    /// database does not exist yet - EF's Migrate()/EnsureCreated() creates it on
    /// first use - and is deliberately never dropped: the container is disposed at
    /// the end of the run, so per-test cleanup would only add time and flake surface.
    /// </summary>
    public string CreateConnectionString(string databaseName) =>
        new SqlConnectionStringBuilder(Container.GetConnectionString())
        {
            InitialCatalog = databaseName,
        }.ConnectionString;

    public static string NewDatabaseName() => $"rocketwiki_{Guid.NewGuid():N}";

    public async Task DisposeAsync()
    {
        if (_container is null)
        {
            return;
        }

        await TryDumpContainerLogsAsync(_container);
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Always writes the engine's stdout/stderr to disk before the container is
    /// removed - when a CI run fails, these logs are the only way to see why SQL
    /// Server itself was unhappy (FTS install, fdhost, population). The CI job
    /// uploads the directory as an artifact; ROCKETWIKI_SQLSERVER_TEST_LOGS points
    /// it somewhere the workflow can find, and local runs land in the temp dir.
    /// Best-effort by design: log capture must never turn a passing run into a
    /// failing one.
    /// </summary>
    private static async Task TryDumpContainerLogsAsync(MsSqlContainer container)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("ROCKETWIKI_SQLSERVER_TEST_LOGS");
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(Path.GetTempPath(), "rocketwiki-sqlserver-tests");
            }

            Directory.CreateDirectory(directory);
            var (stdout, stderr) = await container.GetLogsAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "mssql-stdout.log"), stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, "mssql-stderr.log"), stderr);
        }
        catch
        {
            // Diagnostics only - never fail the run over log collection.
        }
    }
}

/// <summary>
/// Single collection so all SQL Server test classes share one container. xUnit runs
/// classes within a collection sequentially, which also keeps the container's load
/// predictable (SQL Server in a small CI container behaves badly under parallel
/// database creation).
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "SqlServer container";
}
