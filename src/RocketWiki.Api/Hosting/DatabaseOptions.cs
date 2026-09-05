namespace RocketWiki.Api.Hosting;

/// <summary>
/// The <c>Database</c> configuration section (design.md §15 "Migrations must leave
/// startup"). Bound in Program.cs like every other options family; nothing here needs
/// validation — a boolean binds or fails to bind — so the registration is the plain
/// <c>AddOptions().Bind()</c> without the DataAnnotations chain.
///
/// <para>Read AFTER <c>Build()</c>, from the container: the migration decision is the
/// first thing the built host does, so it needs nothing eager and honours a test host's
/// configuration (the API test factory sets it false because it builds its schema with
/// <c>EnsureCreated</c>). The connection string stays on <c>GetConnectionString</c> via the
/// Aspire client integration.</para>
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Apply EF Core migrations when the host starts. Default true because local and
    /// Aspire development is always single-instance; the Helm chart hardcodes it false on
    /// the api container and runs migrations as a pre-install Job, because two pods racing
    /// <c>MigrateAsync()</c> is a corruption risk.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}
