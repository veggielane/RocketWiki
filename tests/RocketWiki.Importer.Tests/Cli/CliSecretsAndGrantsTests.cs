using RocketWiki.Core.Enums;
using RocketWiki.Importer.Cli;

namespace RocketWiki.Importer.Tests.Cli;

/// <summary>
/// The operator-facing edges of the importer CLI: where the database credential comes
/// from, and what the tool says before it opens a whole migrated space to everybody.
///
/// <para>These are not remote attack surfaces — the importer is run deliberately by an
/// operator. They are the two places where the tool's shape made the careless thing the
/// easy thing: a connection string that had to go on the command line, and a grant flag
/// whose consequence was invisible until someone looked at the space afterwards.</para>
/// </summary>
public class CliSecretsAndGrantsTests : IDisposable
{
    private readonly string? _originalEnvironmentValue =
        Environment.GetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable);

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "rocketwiki-cli-secrets-tests", Guid.NewGuid().ToString("N"));

    public CliSecretsAndGrantsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable, _originalEnvironmentValue);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // --- Where the connection string comes from -------------------------------------------

    /// <summary>
    /// A file, for a Kubernetes/Docker secret mount. The value never touches argv, where
    /// `ps` and /proc/&lt;pid&gt;/cmdline would expose it to any other local user — and
    /// where it also lands in shell history and crash dumps.
    ///
    /// <para>Mutation-tested: delete the <c>--connection-string-file</c> branch from
    /// ResolveConnectionString and this returns null (a usage error) instead.</para>
    /// </summary>
    [Fact]
    public void Connection_string_can_come_from_a_file()
    {
        var path = Path.Combine(_tempDir, "connection-string");
        // Trailing newline on purpose: every editor and every k8s projection adds one, and
        // a connection string with one appended fails in a way that names neither.
        File.WriteAllText(path, "Server=db;Database=RocketWiki;\n");

        var options = ParseRealRun("--connection-string-file", path);

        Assert.NotNull(options);
        Assert.Equal("Server=db;Database=RocketWiki;", options.ConnectionString);
    }

    /// <summary>The default when neither flag is given: no secret in argv at all.</summary>
    [Fact]
    public void Connection_string_falls_back_to_the_environment_variable()
    {
        Environment.SetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable, "Server=env-db;");

        var options = ParseRealRun();

        Assert.NotNull(options);
        Assert.Equal("Server=env-db;", options.ConnectionString);
    }

    /// <summary>Explicit precedence, not a merge: an operator who passes the flag gets
    /// exactly what they passed and nothing quietly overrides it.</summary>
    [Fact]
    public void An_explicit_flag_wins_over_the_environment_variable()
    {
        Environment.SetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable, "Server=env-db;");

        var options = ParseRealRun("--connection-string", "Server=argv-db;");

        Assert.NotNull(options);
        Assert.Equal("Server=argv-db;", options.ConnectionString);
    }

    /// <summary>A file that is missing or empty is a usage error, not a half-configured
    /// run that connects somewhere unintended.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_or_empty_connection_string_file_is_a_usage_error(bool createEmpty)
    {
        Environment.SetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable, null);
        var path = Path.Combine(_tempDir, "empty-connection-string");
        if (createEmpty)
        {
            File.WriteAllText(path, "   \n");
        }

        Assert.Null(ParseRealRun("--connection-string-file", path));
    }

    // --- --local-instance-id is required for a real run (design.md §12) --------------------

    /// <summary>
    /// It used to default to "standalone" — the one value that cannot be right anywhere but
    /// a test. A space's <c>OriginInstanceId</c> is what <c>Space.IsReplicaOf</c> compares
    /// against the API's configured identity, so an import without the flag against an API
    /// configured with any other id produces spaces that are permanently READ-ONLY replicas
    /// of an instance that does not exist.
    ///
    /// <para>Mutation-tested: restore the <c>?? "standalone"</c> default (drop
    /// <c>localInstanceId is null</c> from the real-run check) and this stops failing.</para>
    /// </summary>
    [Fact]
    public void Real_run_without_local_instance_id_is_a_usage_error()
    {
        Environment.SetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable, "Server=env-db;");

        Assert.Null(CliArgumentParser.Parse([
            "--export", "export.zip",
            "--space-key", "ENG",
            "--attachments-root", _tempDir,
            "--acting-user-id", Guid.NewGuid().ToString(),
            "--importer-principal-id", "importer-sub",
            "--grant-role", "editor",
            "--grant-expression", """{"group":"engineering"}""",
        ]));
    }

    /// <summary>A dry run writes nothing, so it needs no identity — and must not have
    /// grown a new required flag.</summary>
    [Fact]
    public void Dry_run_still_needs_no_local_instance_id_and_no_connection_string()
    {
        Environment.SetEnvironmentVariable(CliArgumentParser.ConnectionStringVariable, null);

        var options = CliArgumentParser.Parse(["--export", "export.zip", "--space-key", "ENG", "--dry-run"]);

        Assert.NotNull(options);
        Assert.True(options.DryRun);
    }

    // --- The wide-grant warning ------------------------------------------------------------

    /// <summary>
    /// <c>--grant-expression '{"everyone": true}'</c> makes a whole migrated Confluence
    /// space readable by every authenticated principal on the instance — in one flag, and
    /// until now with nothing anywhere saying so. A warning rather than a refusal: it is a
    /// legitimate choice for genuinely general content, and a hard block would only teach
    /// people to work around it. What was missing was visibility, not permission.
    ///
    /// <para>Mutation-tested: remove the WarnOnWideGrant call (or its `everyone` match) and
    /// this fails with an empty output.</para>
    /// </summary>
    [Theory]
    [InlineData("""{"everyone": true}""")]
    [InlineData("""{ "everyone" : true }""")] // whitespace cannot slip past a parsed match
    public void Everyone_grants_are_warned_about_by_name(string expression)
    {
        using var output = new StringWriter();

        ImporterCli.WarnOnWideGrant(expression, "ENG", output);

        var text = output.ToString();
        Assert.Contains("WIDE GRANT", text, StringComparison.Ordinal);
        Assert.Contains("'ENG'", text, StringComparison.Ordinal);
        Assert.Contains("EVERY", text, StringComparison.Ordinal);
    }

    /// <summary>Everything else is silent — a warning that fires on ordinary grants is a
    /// warning nobody reads.</summary>
    [Theory]
    [InlineData("""{"group": "engineering"}""")]
    [InlineData("""{"everyone": false}""")]
    [InlineData("""{"attr": {"clearance": "SECRET"}}""")]
    [InlineData("not json at all")] // the import's own grant preflight refuses this, better
    public void Ordinary_grants_produce_no_warning(string expression)
    {
        using var output = new StringWriter();

        ImporterCli.WarnOnWideGrant(expression, "ENG", output);

        Assert.Equal(string.Empty, output.ToString());
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>A complete real-run argument list, plus whatever the caller adds. Every
    /// required flag but the connection string is present, so a null result is always about
    /// the thing under test.</summary>
    private CliOptions? ParseRealRun(params string[] extra) =>
        CliArgumentParser.Parse([
            "--export", "export.zip",
            "--space-key", "ENG",
            "--attachments-root", _tempDir,
            "--acting-user-id", Guid.NewGuid().ToString(),
            "--importer-principal-id", "importer-sub",
            "--grant-role", nameof(SpaceRole.Editor),
            "--grant-expression", """{"group":"engineering"}""",
            "--local-instance-id", "low-instance",
            .. extra,
        ]);
}
