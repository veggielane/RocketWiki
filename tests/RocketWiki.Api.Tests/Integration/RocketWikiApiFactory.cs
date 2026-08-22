using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RocketWiki.Data;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §14's integration tier: the real API — real GraphQL schema, real
/// ASP.NET Core pipeline — running against EF Core on SQLite (in-memory,
/// fresh schema per factory instance, mirroring RocketWiki.Data.Tests'
/// SqliteTestBase convention) and <c>FileSystemFileStorage</c> in a temp
/// directory. No containers, no real Keycloak, no real SQL Server.
///
/// A green run here forces the EF model and any future LINQ to stay
/// provider-agnostic — treat a SQLite-only failure as a real finding about
/// the model, not something to route around with a `#if SQLSERVER`.
///
/// Currently shared per test *class* (standard <c>IClassFixture</c> usage —
/// constructing the host is expensive), so tests within one class share
/// database state. That's fine for read-only queries like `me`; the first
/// test project to add a *mutation* here should either give it its own
/// factory instance or wrap each test in a transaction it rolls back, rather
/// than relying on execution order.
/// </summary>
public sealed class RocketWikiApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _attachmentsRoot =
        Path.Combine(Path.GetTempPath(), "rocketwiki-api-tests-" + Guid.NewGuid());

    public RocketWikiApiFactory()
    {
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        // busy_timeout matters once NotificationsHubTests joined this fixture: a
        // SignalR LongPolling connection holds a "poll" GET open concurrently with a
        // "send" POST invoking a hub method, and both can touch this factory's single
        // shared :memory: connection at genuinely the same time (unlike a normal
        // request/response HTTP test, which never overlaps two calls against it).
        // Without this, SQLite fails immediately with "database is locked" instead of
        // briefly waiting for the other side's short-lived transaction to finish -
        // purely additive, doesn't change behaviour for any test that never contends.
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not a dedicated "Testing" environment: ServiceDefaults only maps
        // /health and /alive under IsDevelopment() (see RocketWiki.ServiceDefaults
        // Extensions.cs — non-dev health endpoints have security implications),
        // and this fixture exists partly to exercise those endpoints.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // EnsureCreated (below) builds schema from the live model, not
                // the SQL-Server-targeted checked-in migrations — same reason
                // RocketWiki.Data.Tests doesn't call Database.Migrate() either.
                ["Database:MigrateOnStartup"] = "false",
                ["FileStorage:Provider"] = "FileSystem",
                ["FileStorage:FileSystem:Root"] = _attachmentsRoot,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Aspire's AddSqlServerDbContext registers a *pooled* context (a
            // singleton IDbContextPool<RocketWikiDbContext> consuming a scoped
            // DbContextOptions<RocketWikiDbContext>, plus related internal
            // EF Core service types). Removing only DbContextOptions<T> leaves
            // that singleton pool behind, wired to nothing — ASP.NET Core's
            // ValidateOnBuild (on by default in Development) catches exactly
            // this as an invalid singleton-consuming-scoped lifetime. Rather
            // than chase EF Core's internal type names one at a time, remove
            // every registration closed over RocketWikiDbContext and start clean.
            var descriptors = services
                .Where(d => d.ServiceType.IsGenericType
                    && d.ServiceType.GetGenericArguments().Contains(typeof(RocketWikiDbContext)))
                .ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<RocketWikiDbContext>();

            services.AddDbContext<RocketWikiDbContext>(options => options.UseSqlite(_connection));

            // Replaces "Bearer" (real Keycloak JWT validation) with the fake
            // handler as the default scheme, so tests never need a real token.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>().Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();

            if (Directory.Exists(_attachmentsRoot))
            {
                Directory.Delete(_attachmentsRoot, recursive: true);
            }
        }
    }
}
