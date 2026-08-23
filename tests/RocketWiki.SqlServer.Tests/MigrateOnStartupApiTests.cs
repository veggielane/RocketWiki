using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Tests.Integration;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// Closes the README "Explicitly unverified" item: "`Database:MigrateOnStartup`'s
/// happy path is unverified even though its failure path (an unreachable server) was
/// confirmed to fail loudly rather than silently." This boots the REAL API host
/// (WebApplicationFactory over Program) against the container with MigrateOnStartup
/// left at its default of true - so Program's own startup MigrateAsync creates the
/// database and applies every checked-in migration - then proves /graphql answers,
/// including an authenticated request whose JIT provisioning writes a real row.
///
/// Unlike RocketWikiApiFactory (the SQLite tier), this factory does NOT rip out the
/// DbContext registrations: Program's Aspire AddSqlServerDbContext wiring is part of
/// what has never run against a real server, so it must stay. Only two things are
/// overridden, both environmental: the connection string (pointed at the container)
/// and authentication (the same TestAuthHandler stand-in the SQLite tier uses -
/// there is still no Keycloak here, and that remains explicitly unverified).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MigrateOnStartupApiTests
{
    private readonly SqlServerContainerFixture _fixture;

    public MigrateOnStartupApiTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SqlServerFact]
    public async Task MigrateOnStartup_HappyPath_AppBoots_MigratesFromZero_AndGraphQlAnswers()
    {
        var connectionString = _fixture.CreateConnectionString(SqlServerContainerFixture.NewDatabaseName());
        await using var factory = new SqlServerApiFactory(connectionString);

        // CreateClient boots the host; Program's MigrateOnStartup block runs here.
        // If the migration path is broken this line throws - loudly, as designed.
        var client = factory.CreateClient();

        // Anonymous request: the pipeline answers without a database-backed identity.
        var anonymous = await client.PostGraphQLAsync("{ me { isAuthenticated } }");
        Assert.False(anonymous.RootElement
            .GetProperty("data").GetProperty("me").GetProperty("isAuthenticated").GetBoolean());

        // Authenticated request: exercises JIT provisioning's INSERT against the
        // migrated schema - a real write through the real pipeline.
        client.SetTestUser(
            sub: "sqlserver-tier-user",
            email: "sqlserver-tier@example.test",
            name: "SqlServer Tier User",
            groups: ["engineering"]);
        var authenticated = await client.PostGraphQLAsync("{ me { id isAuthenticated } }");
        var me = authenticated.RootElement.GetProperty("data").GetProperty("me");
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("sqlserver-tier-user", me.GetProperty("id").GetString());

        // The startup path - not this test - must have applied every migration...
        var options = new DbContextOptionsBuilder<RocketWikiDbContext>()
            .UseSqlServer(connectionString).Options;
        using var verification = new RocketWikiDbContext(options);
        Assert.Empty(await verification.Database.GetPendingMigrationsAsync());

        // ...and the JIT-provisioned user row genuinely landed in SQL Server.
        Assert.True(await verification.Users.AnyAsync(u => u.Subject == "sqlserver-tier-user"));
    }
}

/// <summary>See <see cref="MigrateOnStartupApiTests"/> for what is (and is not) overridden.</summary>
internal sealed class SqlServerApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _attachmentsRoot =
        Path.Combine(Path.GetTempPath(), "rocketwiki-sqlserver-api-tests-" + Guid.NewGuid());

    public SqlServerApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development for the same reason as RocketWikiApiFactory: ServiceDefaults
        // maps health endpoints under IsDevelopment(), and JWT metadata over HTTP is
        // only tolerated there.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The name Program's AddSqlServerDbContext binds ("rocketwiki") - in
                // production Aspire injects this; here the container provides it.
                // Database:MigrateOnStartup is deliberately NOT set: its default of
                // true IS the behavior under test.
                ["ConnectionStrings:rocketwiki"] = _connectionString,
                ["FileStorage:Provider"] = "FileSystem",
                ["FileStorage:FileSystem:Root"] = _attachmentsRoot,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_attachmentsRoot))
        {
            Directory.Delete(_attachmentsRoot, recursive: true);
        }
    }
}
