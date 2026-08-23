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
/// ASP.NET Core pipeline — running against EF Core on SQLite (a temp file,
/// fresh schema per factory instance, deleted on dispose) and
/// <c>FileSystemFileStorage</c> in a temp directory. No containers, no real
/// Keycloak, no real SQL Server.
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
    // A temp *file* rather than a shared :memory: connection: once
    // NotificationsHubTests joined this fixture, a SignalR LongPolling connection
    // holds a "poll" GET open concurrently with a "send" POST invoking a hub
    // method, so two request scopes genuinely overlap — and a single
    // SqliteConnection instance is not thread-safe under that overlap (symptom:
    // "database is locked" and garbled "not an error" SqliteExceptions thrown from
    // EF's own connection initialization; PRAGMA busy_timeout cannot help, because
    // the problem is two threads on one native handle, not lock contention). With
    // a file, every DbContext opens its own pooled connection and
    // Microsoft.Data.Sqlite's built-in busy retry serializes the writes.
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "rocketwiki-api-tests-" + Guid.NewGuid() + ".db");
    private readonly string _attachmentsRoot =
        Path.Combine(Path.GetTempPath(), "rocketwiki-api-tests-" + Guid.NewGuid());

    // Foreign Keys=True replaces the PRAGMA the old single shared connection set
    // once at construction — as a connection-string keyword it applies to every
    // pooled connection this factory's contexts open.
    private string ConnectionString => $"Data Source={_databasePath};Foreign Keys=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not a dedicated "Testing" environment: ServiceDefaults maps /health and
        // /alive under IsDevelopment() (or the explicit HealthEndpoints:Enabled
        // opt-in — see RocketWiki.ServiceDefaults Extensions.cs; non-dev health
        // endpoints have security implications), and this fixture exists partly to
        // exercise those endpoints. HealthEndpointGateTests covers the non-dev gate
        // by layering UseEnvironment("Production") on top of this fixture.
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

            // UseLocalInstanceId mirrors Program.cs's own wiring (which this
            // registration replaces wholesale): "standalone" is Program's Instance:Id
            // default, and every fixture that seeds a native space uses
            // OriginInstanceId = "standalone" to match. Without it, mutations on
            // exported spaces would throw - the sync outbox writer refuses to journal
            // when it cannot verify ownership (design.md §12).
            services.AddDbContext<RocketWikiDbContext>(options =>
                options.UseSqlite(ConnectionString).UseLocalInstanceId("standalone"));

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
            // Pooled connections keep the file handle open, which on Windows makes
            // the delete below fail — drain this database's pool first.
            SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));

            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }

            if (Directory.Exists(_attachmentsRoot))
            {
                Directory.Delete(_attachmentsRoot, recursive: true);
            }
        }
    }
}
