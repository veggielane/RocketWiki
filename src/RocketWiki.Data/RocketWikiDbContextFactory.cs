using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RocketWiki.Data;

/// <summary>
/// Used by `dotnet ef migrations add` / `dotnet ef database update` at design time, and by
/// the migration BUNDLE (`efbundle`) the Helm chart runs as a Job. The API wires up the
/// real connection string via Aspire service discovery at runtime (design.md §15) — this
/// factory exists so RocketWiki.Data can produce and apply migrations without depending on
/// RocketWiki.Api.
///
/// <para><b>The connection string comes from the environment, not from argv.</b> The
/// migration Job used to pass it as <c>--connection "$(ROCKETWIKI_CONNECTIONSTRING)"</c>,
/// which put the database credentials into the container's argv — readable in
/// <c>kubectl describe pod</c>, in <c>/proc/&lt;pid&gt;/cmdline</c> to anything else in the
/// pod, and in whatever a container runtime records about a process. Reading the same value
/// from an environment variable keeps it out of all three: the pod spec carries only a
/// <c>secretKeyRef</c>, and the value exists solely inside the container's own
/// environment.</para>
///
/// <para>The local placeholder is kept as the fallback because <c>dotnet ef migrations
/// add</c> on a developer's machine has no such variable and needs a provider, not a live
/// database — EF only needs to know it is targeting SQL Server to scaffold. A Job that
/// somehow reached this fallback would try to connect to <c>localhost</c> inside its own
/// container and fail immediately, which is the direction that fails safe.</para>
/// </summary>
public class RocketWikiDbContextFactory : IDesignTimeDbContextFactory<RocketWikiDbContext>
{
    /// <summary>
    /// The environment variables consulted, in order. The second is the standard
    /// ASP.NET Core configuration spelling of <c>ConnectionStrings:rocketwiki</c>, so a
    /// Secret whose key is already that name works with no extra mapping.
    /// </summary>
    public static readonly string[] ConnectionStringVariables =
        ["ROCKETWIKI_CONNECTIONSTRING", "ConnectionStrings__rocketwiki"];

    private const string LocalDesignTimePlaceholder =
        "Server=localhost;Database=RocketWiki;Trusted_Connection=True;TrustServerCertificate=True;";

    public RocketWikiDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RocketWikiDbContext>();
        optionsBuilder.UseSqlServer(ResolveConnectionString());
        return new RocketWikiDbContext(optionsBuilder.Options);
    }

    private static string ResolveConnectionString()
    {
        foreach (var variable in ConnectionStringVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return LocalDesignTimePlaceholder;
    }
}
